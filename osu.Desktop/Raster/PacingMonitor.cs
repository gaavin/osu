// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#if RASTER_METRICS
using System;
using System.Text;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Measures frame pacing as it reaches the screen: for each band of scanlines, how evenly the scene shown there advances from one refresh to the next.
    /// </summary>
    /// <remarks>
    /// With several presents a refresh, different parts of the screen show different frames, so presents per second and skipped slices
    /// say little about how smooth motion looks. What motion looks like in a band is set by how much scene time passes between one refresh
    /// scanning it out and the next. Moving at a steady speed, that should be exactly one refresh period each time. Anything else shows as
    /// judder, and a band that shows the same frame twice stutters.
    ///
    /// A frame covers the scanlines from where it tore down to where the next one tore. Where it tore is taken from when it was presented,
    /// moved by the steered tear line offset, and its scene time from when the draw thread woke to draw it. Draw thread only.
    /// </remarks>
    internal sealed class PacingMonitor
    {
        public const int BAND_COUNT = 8;

        /// <summary>
        /// How far a band's step may be from a refresh period before it counts as uneven.
        /// </summary>
        private const long uneven_ns = 500_000;

        private const long hitch_ns = 1_000_000;

        private const int present_history = 64;

        private readonly long[] tears = new long[present_history];
        private readonly long[] scenes = new long[present_history];
        private int nextPresent;
        private int presentCount;

        private readonly long[] lastScene = new long[BAND_COUNT];
        private long nextRefresh;

        private readonly DurationWindow errors = new DurationWindow(4096);

        private int intervalRefreshes;
        private int intervalUneven;
        private int intervalHitches;
        private int intervalRepeats;
        private int intervalGaps;
        private readonly long[] intervalBandMax = new long[BAND_COUNT];
        private readonly int[] intervalBandUneven = new int[BAND_COUNT];

        /// <summary>
        /// Draw thread, after each timed present.
        /// </summary>
        /// <param name="timing">The refresh timing the present was planned against.</param>
        /// <param name="presentNs">When the swap started.</param>
        /// <param name="offsetLines">The tear line offset the present was steered by, in scanlines.</param>
        /// <param name="sceneNs">When the draw thread woke to draw the frame, which is as new as its scene can be.</param>
        public void NotePresent(DrmVBlankClock.Timing timing, long presentNs, double offsetLines, long sceneNs)
        {
            double lineNs = (double)timing.PeriodNs / timing.VTotal;
            long tearNs = presentNs - (long)(offsetLines * lineNs);

            // Refreshes whose last band is scanned out before this tear are settled: no later present can reach them.
            if (nextRefresh == 0 || tearNs - nextRefresh > 1_000_000_000)
            {
                nextRefresh = timing.VBlankNs + (tearNs - timing.VBlankNs + timing.PeriodNs - 1) / timing.PeriodNs * timing.PeriodNs;
                Array.Clear(lastScene);
            }

            while (nextRefresh + (long)(timing.VDisplay * lineNs) < tearNs)
            {
                evaluate(nextRefresh, timing, lineNs);
                nextRefresh += timing.PeriodNs;
            }

            tears[nextPresent] = tearNs;
            scenes[nextPresent] = sceneNs;
            nextPresent = (nextPresent + 1) % present_history;
            presentCount = Math.Min(presentCount + 1, present_history);
        }

        private void evaluate(long refreshStart, DrmVBlankClock.Timing timing, double lineNs)
        {
            intervalRefreshes++;

            for (int band = 0; band < BAND_COUNT; band++)
            {
                long scanNs = refreshStart + (long)((band + 0.5) * timing.VDisplay / BAND_COUNT * lineNs);
                long scene = sceneShownAt(scanNs);

                if (scene == 0)
                    continue;

                long previous = lastScene[band];
                lastScene[band] = scene;

                if (previous == 0)
                    continue;

                long step = scene - previous;

                if (step == 0)
                {
                    intervalRepeats++;
                    continue;
                }

                // A pause, or a stall long enough that it is not pacing.
                if (step > 3 * timing.PeriodNs)
                {
                    intervalGaps++;
                    continue;
                }

                long error = Math.Abs(step - timing.PeriodNs);

                errors.Add(error);
                intervalBandMax[band] = Math.Max(intervalBandMax[band], error);

                if (error > uneven_ns)
                {
                    intervalUneven++;
                    intervalBandUneven[band]++;
                }

                if (error > hitch_ns)
                    intervalHitches++;
            }
        }

        /// <summary>
        /// The scene time of the newest frame that had torn by the given time, or 0 if none recorded has.
        /// </summary>
        private long sceneShownAt(long ns)
        {
            for (int i = 1; i <= presentCount; i++)
            {
                int index = (nextPresent - i + present_history) % present_history;

                if (tears[index] <= ns)
                    return scenes[index];
            }

            return 0;
        }

        /// <summary>
        /// A log line for the interval just ended, which starts the next.
        /// </summary>
        public string TakeIntervalSummary()
        {
            var bands = new StringBuilder();

            for (int band = 0; band < BAND_COUNT; band++)
            {
                if (band > 0)
                    bands.Append(' ');

                bands.Append($"{intervalBandMax[band] / 1e6:0.00}({intervalBandUneven[band]})");
            }

            int bandRefreshes = intervalRefreshes * BAND_COUNT;

            string summary = $"Raster sync pacing: {intervalRefreshes} refreshes in {BAND_COUNT} bands of scanlines. "
                             + $"Scene time stepped by a refresh ± {errors.Percentile(0.5) / 1e6:0.000}/{errors.Percentile(0.99) / 1e6:0.000} ms at p50/p99 over recent refreshes; "
                             + $"this second {intervalUneven} of {bandRefreshes} band steps were more than {uneven_ns / 1e6:0.0} ms off, {intervalHitches} more than {hitch_ns / 1e6:0.0} ms, "
                             + $"{intervalRepeats} showed the same frame again and {intervalGaps} spanned a stall. "
                             + $"Worst step error by band from the top, ms (steps over {uneven_ns / 1e6:0.0} ms): {bands}.";

            intervalRefreshes = 0;
            intervalUneven = 0;
            intervalHitches = 0;
            intervalRepeats = 0;
            intervalGaps = 0;
            Array.Clear(intervalBandMax);
            Array.Clear(intervalBandUneven);

            return summary;
        }
    }
}
#endif

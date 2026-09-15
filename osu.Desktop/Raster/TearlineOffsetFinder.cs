// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using osu.Framework.Logging;
using osu.Framework.Platform;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Finds the tear line offset from how long the compositor takes to flip frames, and keeps refitting it while the user plays.
    /// </summary>
    /// <remarks>
    /// A present timed for a scanline tears as far down the screen as scanout gets while the compositor flips it.
    /// The offset centres the blanking interval on the span of flip times, as long as the blanking interval lasts, that holds the most flips.
    /// During a play it is refitted every second or so to the play's recent flips, kept as a decaying histogram of swap-to-flip times.
    /// Each finished play's histogram is kept too, so the next play starts from the offset found across recent plays.
    /// </remarks>
    internal sealed class TearlineOffsetFinder
    {
        public sealed record Suggestion(int OffsetLines, double Coverage, double MedianLatencyMs, int Plays, long Flips);

        private const string filename = @"raster-sync-flips.json";

        private const long bin_ns = 10_000;
        private const int bin_count = 1000;

        private const int plays_kept = 20;
        private const long min_play_flips = 200;

        /// <summary>
        /// Flips between refits during a play. About a second at 144 Hz, with the probe timing every third present.
        /// </summary>
        private const int steer_interval_flips = 48;

        /// <summary>
        /// Recent flips needed before a play steers the tear line from its own flips.
        /// </summary>
        private const double min_steer_flips = 96;

        /// <summary>
        /// How much of the recent histogram carries over each refit. Keeps about the last 240 flips.
        /// </summary>
        private const double steer_decay = 0.8;

        // Tearing flips follow the swap within a couple of milliseconds. A compositor holding frames for vblank takes most of a refresh.
        private const double max_median_latency_ms = 3;
        private const double max_latency_spread_ms = 1.5;

        private sealed class Play
        {
            public DateTimeOffset Date { get; set; }

            public string Display { get; set; } = string.Empty;

            public long Flips { get; set; }

            public long Misses { get; set; }

            /// <summary>
            /// Timed frames the next frame was swapped after before they flipped, so the compositor may never have shown them. Not in <see cref="Flips"/>.
            /// </summary>
            public long Overtaken { get; set; }

            /// <summary>
            /// Flips per 10 µs bin of swap-to-flip time. Flips past the last bin count towards <see cref="Flips"/> only.
            /// </summary>
            public Dictionary<int, long> Histogram { get; set; } = new Dictionary<int, long>();
        }

        private sealed class Recording
        {
            public List<Play> Plays { get; set; } = new List<Play>();
        }

        private sealed record Cached(string Display, int VDisplay, int VTotal, int Version, Suggestion? Value);

        private sealed record Steering(string Display, Suggestion Value);

        private readonly Storage storage;
        private readonly object sync = new object();
        private readonly object saveSync = new object();
        private readonly Recording recording;

        private int version;
        private volatile Cached? cached;
        private volatile string? lastRejection;

        // The play being recorded, guarded by sync.
        private readonly long[] bins = new long[bin_count];
        private readonly double[] recent = new double[bin_count];
        private long flips;
        private double recentFlips;
        private int flipsSinceSteer;
        private long misses;
        private long overtaken;
        private string? display;
        private bool playing;

        private volatile Steering? steering;
        private volatile string? steeringRejection;

        public TearlineOffsetFinder(Storage storage)
        {
            this.storage = storage;
            recording = load();
        }

        /// <summary>
        /// Update thread.
        /// </summary>
        public void BeginPlay()
        {
            lock (sync)
            {
                reset(null);
                playing = true;
            }
        }

        /// <summary>
        /// Keeps the play that just ended, if it recorded enough flips and they look like tearing. Update thread.
        /// </summary>
        public void EndPlay(bool waitForSave = false)
        {
            lock (sync)
            {
                if (!playing)
                    return;

                playing = false;
                steering = null;
                steeringRejection = null;

                if (display == null || flips < min_play_flips)
                    return;

                double[] histogram = new double[bin_count];
                for (int i = 0; i < bin_count; i++)
                    histogram[i] = (double)bins[i] / flips;

                double median = percentileMs(histogram, 0.5);
                double spread = percentileMs(histogram, 0.75) - percentileMs(histogram, 0.25);

                if (median > max_median_latency_ms)
                {
                    lastRejection = $"The last play was left out: its frames took {median:0.00} ms to reach the display, so the compositor was holding them for vblank instead of tearing.";
                    return;
                }

                if (spread > max_latency_spread_ms)
                {
                    lastRejection = $"The last play was left out: the compositor's flip times varied by {spread:0.00} ms.";
                    return;
                }

                var play = new Play
                {
                    Date = DateTimeOffset.Now,
                    Display = display,
                    Flips = flips,
                    Misses = misses,
                    Overtaken = overtaken,
                };

                for (int i = 0; i < bin_count; i++)
                {
                    if (bins[i] > 0)
                        play.Histogram[i] = bins[i];
                }

                recording.Plays.Add(play);

                var playsOfDisplay = recording.Plays.Where(p => p.Display == play.Display).ToList();
                foreach (var old in playsOfDisplay.Take(playsOfDisplay.Count - plays_kept))
                    recording.Plays.Remove(old);

                lastRejection = null;
                Interlocked.Increment(ref version);
            }

            save(waitForSave);
        }

        /// <summary>
        /// Probe thread.
        /// </summary>
        public void AddFlip(DrmVBlankClock.Timing timing, long latencyNs)
        {
            lock (sync)
            {
                if (!playing || latencyNs < 0)
                    return;

                // A play at another mode than it started in only counts from the change on.
                if (timing.Display != display)
                    reset(timing.Display);

                long bin = latencyNs / bin_ns;

                if (bin < bin_count)
                {
                    bins[bin]++;
                    recent[bin]++;
                }

                flips++;
                recentFlips++;

                if (++flipsSinceSteer >= steer_interval_flips)
                {
                    flipsSinceSteer = 0;
                    steer(timing);
                }
            }
        }

        /// <summary>
        /// Probe thread.
        /// </summary>
        public void AddMiss(DrmVBlankClock.Timing timing)
        {
            lock (sync)
            {
                if (playing && timing.Display == display)
                    misses++;
            }
        }

        /// <summary>
        /// Probe thread.
        /// </summary>
        public void AddOvertaken(DrmVBlankClock.Timing timing)
        {
            lock (sync)
            {
                if (playing && timing.Display == display)
                    overtaken++;
            }
        }

        /// <summary>
        /// Discards the recorded plays, and the flips of the play in progress.
        /// </summary>
        public void Forget()
        {
            lock (sync)
            {
                recording.Plays.Clear();
                reset(display);
                lastRejection = null;
                Interlocked.Increment(ref version);
            }

            save(false);
        }

        /// <summary>
        /// The offset to aim the tear line at: the one steered from the play in progress, else the one found from previous plays,
        /// or null with neither at the timing's mode. Draw thread.
        /// </summary>
        public int? GetOffset(DrmVBlankClock.Timing timing)
        {
            var current = steering;

            if (current != null && current.Display == timing.Display)
                return current.Value.OffsetLines;

            return fromPreviousPlays(timing)?.OffsetLines;
        }

        /// <summary>
        /// Update thread.
        /// </summary>
        public string Describe(DrmVBlankClock.Timing? timing)
        {
            string text;

            if (timing == null)
                text = @"Waiting for the display.";
            else if (steering is Steering current && current.Display == timing.Display)
            {
                text = $"Steering the tear line to {current.Value.OffsetLines} lines from this play's recent flips. "
                       + $"The compositor takes {current.Value.MedianLatencyMs:0.00} ms to flip a frame (median), and {current.Value.Coverage:0%} of recent flips tear inside the blanking interval.";
            }
            else if (fromPreviousPlays(timing) is Suggestion suggestion)
            {
                text = $"Plays start at {suggestion.OffsetLines} lines, found from {suggestion.Flips} flips over {suggestion.Plays} plays, then steer from their own flips. "
                       + $"The compositor takes {suggestion.MedianLatencyMs:0.00} ms to flip a frame (median), and {suggestion.Coverage:0%} of flips tear inside the blanking interval at this offset.";
            }
            else
                text = $"No plays recorded at {timing.Display} yet. Plays steer the tear line once they have recorded a couple of seconds of flips.";

            if (steeringRejection != null)
                text += $" {steeringRejection}";

            if (lastRejection != null)
                text += $" {lastRejection}";

            lock (sync)
            {
                if (playing)
                {
                    text += $" Recording this play: {flips} flips.";

                    if (overtaken > 0)
                        text += $" {overtaken} timed frames were overtaken by the next frame before they flipped, so they may never have been shown.";
                }
            }

            return text;
        }

        /// <summary>
        /// Refits the offset to the play's recent flips. Probe thread, holding sync.
        /// </summary>
        private void steer(DrmVBlankClock.Timing timing)
        {
            if (recentFlips < min_steer_flips)
                return;

            double[] histogram = new double[bin_count];

            for (int i = 0; i < bin_count; i++)
            {
                histogram[i] = recent[i] / recentFlips;
                recent[i] *= steer_decay;
            }

            recentFlips *= steer_decay;

            double median = percentileMs(histogram, 0.5);
            double spread = percentileMs(histogram, 0.75) - percentileMs(histogram, 0.25);

            // The tear line holds where it is until flips look like tearing again.
            if (median > max_median_latency_ms)
            {
                steeringRejection = $"Holding the tear line: recent frames took {median:0.00} ms to reach the display, so the compositor is holding them for vblank instead of tearing.";
                return;
            }

            if (spread > max_latency_spread_ms)
            {
                steeringRejection = $"Holding the tear line: the compositor's recent flip times vary by {spread:0.00} ms.";
                return;
            }

            steeringRejection = null;
            steering = new Steering(timing.Display, fit(histogram, timing, 0, flips));
        }

        /// <summary>
        /// The offset found from previous plays at a mode, or null if none has been recorded at it. Cached, so cheap enough for the draw thread.
        /// </summary>
        private Suggestion? fromPreviousPlays(DrmVBlankClock.Timing timing)
        {
            var current = cached;

            if (current != null && current.Version == Volatile.Read(ref version) && current.Display == timing.Display
                && current.VDisplay == timing.VDisplay && current.VTotal == timing.VTotal)
                return current.Value;

            lock (sync)
            {
                var value = compute(timing);
                cached = new Cached(timing.Display, timing.VDisplay, timing.VTotal, version, value);
                return value;
            }
        }

        private Suggestion? compute(DrmVBlankClock.Timing timing)
        {
            var plays = recording.Plays.Where(p => p.Display == timing.Display && p.Flips > 0).ToList();

            if (plays.Count == 0)
                return null;

            // Every play weighs the same, however long it was.
            double[] combined = new double[bin_count];

            foreach (var play in plays)
            {
                foreach (var (bin, count) in play.Histogram)
                {
                    if (bin >= 0 && bin < bin_count)
                        combined[bin] += (double)count / play.Flips / plays.Count;
                }
            }

            return fit(combined, timing, plays.Count, plays.Sum(p => p.Flips));
        }

        /// <summary>
        /// Fits the offset to a histogram holding the fraction of flips in each bin.
        /// </summary>
        private static Suggestion fit(double[] histogram, DrmVBlankClock.Timing timing, int plays, long totalFlips)
        {
            double lineNs = (double)timing.PeriodNs / timing.VTotal;
            int width = Math.Clamp((int)((timing.VTotal - timing.VDisplay) * lineNs / bin_ns), 1, bin_count);

            double sum = histogram.Take(width).Sum();
            double best = sum;
            var bestStarts = new List<int> { 0 };

            for (int start = 1; start + width <= bin_count; start++)
            {
                sum += histogram[start + width - 1] - histogram[start - 1];

                if (sum > best + 1e-9)
                {
                    best = sum;
                    bestStarts.Clear();
                    bestStarts.Add(start);
                }
                else if (sum > best - 1e-9)
                    bestStarts.Add(start);
            }

            // Centring on the flips inside the span, rather than on the span, keeps the offset from snapping to bins,
            // and leaves room either side when the flips are narrower than the blanking interval.
            int bestStart = bestStarts[bestStarts.Count / 2];
            double insideFlips = 0;
            double insideNs = 0;

            for (int i = bestStart; i < bestStart + width; i++)
            {
                insideFlips += histogram[i];
                insideNs += histogram[i] * (i + 0.5) * bin_ns;
            }

            double centreNs = insideFlips > 0 ? insideNs / insideFlips : (bestStart + width / 2.0) * bin_ns;

            int windowStart = (int)Math.Round(centreNs / bin_ns - width / 2.0);
            double coverage = 0;

            for (int i = Math.Max(0, windowStart); i < Math.Min(bin_count, windowStart + width); i++)
                coverage += histogram[i];

            return new Suggestion(-(int)Math.Round(centreNs / lineNs), coverage, percentileMs(histogram, 0.5), plays, totalFlips);
        }

        /// <summary>
        /// A percentile of a histogram holding the fraction of flips in each bin.
        /// </summary>
        private static double percentileMs(double[] histogram, double fraction)
        {
            double seen = 0;

            for (int i = 0; i < bin_count; i++)
            {
                seen += histogram[i];

                if (seen >= fraction - 1e-9)
                    return (i + 0.5) * bin_ns / 1e6;
            }

            return bin_count * bin_ns / 1e6;
        }

        private void reset(string? newDisplay)
        {
            Array.Clear(bins);
            Array.Clear(recent);
            flips = 0;
            recentFlips = 0;
            flipsSinceSteer = 0;
            misses = 0;
            overtaken = 0;
            display = newDisplay;
            steering = null;
            steeringRejection = null;
        }

        private Recording load()
        {
            try
            {
                if (storage.Exists(filename))
                {
                    using (var stream = storage.GetStream(filename))
                    using (var reader = new StreamReader(stream))
                        return JsonConvert.DeserializeObject<Recording>(reader.ReadToEnd()) ?? new Recording();
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, @"Raster sync could not read its recorded flips");
            }

            return new Recording();
        }

        private void save(bool wait)
        {
            string json;

            lock (sync)
                json = JsonConvert.SerializeObject(recording);

            if (wait)
                write(json);
            else
                Task.Run(() => write(json));
        }

        private void write(string json)
        {
            lock (saveSync)
            {
                try
                {
                    using (var stream = storage.GetStream(filename, FileAccess.Write, FileMode.Create))
                    using (var writer = new StreamWriter(stream))
                        writer.Write(json);
                }
                catch (Exception e)
                {
                    Logger.Error(e, @"Raster sync could not save its recorded flips");
                }
            }
        }
    }
}

// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Times the gameplay clock by when each update frame's scene will be scanned out, rather than by when the update frame happened to run.
    /// </summary>
    /// <remarks>
    /// The update thread runs free, a dozen update frames to a present, and a draw picks up whichever finished last before it woke. So the scene a
    /// present shows was sampled anywhere from nothing to an update frame before the draw, and the draw starts a render margin before a tear line that
    /// itself moves between presents. Measured in play, a scene was 1.71 ms old at present at p50 and 2.55 ms at p99, differently every present, which
    /// moves hit objects unevenly by the same mismatch of clocks that wobbled the cursor.
    ///
    /// Each update frame's gameplay clock is instead run at the time the present that will show it tears, less a running mean of that lead, so every
    /// present shows a scene the same age at its tear line. The mean keeps the clock where it was on average, so judgements and the audio offset are
    /// unchanged but for the variation.
    ///
    /// Which present shows an update frame depends on when the frame publishes its scene against when the draws wake. A frame that publishes once the
    /// planned present's draw has woken is shown by the present after, which isn't planned until the planned one has been swapped, so the draw thread
    /// foresees it as it plans each present. Timing those frames by the planned present instead, as a first version did, left the clock's age as uneven
    /// as sampling it: the gap before a draw wakes is often shorter than an update frame, so the drawn frame had often started before its present was planned.
    /// </remarks>
    internal sealed class SceneTiming
    {
        /// <summary>
        /// Off with <c>OSU_RASTER_SCENE_TIMING=0</c>, which runs the gameplay clock at the time each update frame runs.
        /// </summary>
        public static readonly bool ENABLED = Environment.GetEnvironmentVariable(@"OSU_RASTER_SCENE_TIMING") != @"0";

        /// <summary>
        /// A plan older than this is from before pacing stopped.
        /// </summary>
        private const long stale_plan_ns = 50_000_000;

        /// <summary>
        /// The furthest the clock is moved from real time either way, which bounds what a bad plan can do to judgements.
        /// </summary>
        private const long max_lead_ns = 4_000_000;

        /// <summary>
        /// How many update frames the mean lead is averaged over, about a second and a half at the rates measured in play.
        /// </summary>
        private const double mean_frames = 4096;

        /// <summary>
        /// How many update frames the time from sampling the clock to publishing the scene is averaged over.
        /// </summary>
        private const double publish_frames = 256;

        // Written on the draw thread, read on the update thread, as a sequence: odd while being written.
        private int planSequence;
        private long planWake;
        private long planTear;
        private long nextWake;
        private long nextTear;

        // Update thread only.
        private double meanLeadNs = double.NaN;
        private double publishDelayNs;
        private long sampledAt;
        private long lastSceneNs;

        private readonly UpdateSync updateSync;

        public SceneTiming(UpdateSync updateSync)
        {
            this.updateSync = updateSync;
        }

        /// <summary>
        /// The present just planned, and the one foreseen after it: when each draw wakes, and when each tear line is scanned out. Draw thread.
        /// </summary>
        public void NotePlan(long wake, long tear, long followingWake, long followingTear)
        {
            Interlocked.Increment(ref planSequence);
            Volatile.Write(ref planWake, wake);
            Volatile.Write(ref planTear, tear);
            Volatile.Write(ref nextWake, followingWake);
            Volatile.Write(ref nextTear, followingTear);
            Interlocked.Increment(ref planSequence);
        }

        /// <summary>
        /// How far ahead of real time to run the gameplay clock for the update frame running now, in nanoseconds of real time. Update thread, as the clock is sampled.
        /// </summary>
        public long TakeLeadNs()
        {
            if (!ENABLED)
                return 0;

            long now = Native.MonotonicNs();
            long wake, tear, followingWake, followingTear;
            int sequence;

            do
            {
                sequence = Volatile.Read(ref planSequence);
                wake = Volatile.Read(ref planWake);
                tear = Volatile.Read(ref planTear);
                followingWake = Volatile.Read(ref nextWake);
                followingTear = Volatile.Read(ref nextTear);
            } while ((sequence & 1) != 0 || sequence != Volatile.Read(ref planSequence));

            sampledAt = now;

            long lead = 0;

            if (wake != 0 && now - wake < stale_plan_ns)
            {
                // A draw shows the newest scene published before it wakes.
                long published = now + (long)publishDelayNs;
                long scene;

                if (published < wake)
                    scene = tear;
                else if (published < followingWake)
                    scene = followingTear;
                else
                    scene = followingTear + (published - followingWake);

                long raw = scene - now;

                meanLeadNs = double.IsNaN(meanLeadNs) ? raw : meanLeadNs + (raw - meanLeadNs) / mean_frames;
                lead = Math.Clamp(raw - (long)meanLeadNs, -max_lead_ns, max_lead_ns);
            }

            // Never step the scene back: a frame timed by the foreseen present may have been timed a little past the one actually planned.
            if (lastSceneNs != 0 && now + lead < lastSceneNs)
                lead = lastSceneNs - now;

            lastSceneNs = now + lead;

#if RASTER_METRICS
            updateSync.NoteSceneTime(now, lead);
#endif

            return lead;
        }

        /// <summary>
        /// The update frame has published its scene. Update thread.
        /// </summary>
        public void NoteUpdateFrameEnd()
        {
            if (!ENABLED || sampledAt == 0)
                return;

            long delay = Native.MonotonicNs() - sampledAt;

            sampledAt = 0;

            if (delay < stale_plan_ns)
                publishDelayNs += (delay - publishDelayNs) / publish_frames;
        }
    }
}

// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Times the gameplay clock so each present's scene steps by exactly the time between tear lines, rather than by when update frames happened to run.
    /// </summary>
    /// <remarks>
    /// The update thread runs free, a dozen update frames to a present, and a draw picks up whichever finished last before it woke. So the scene a
    /// present shows was sampled anywhere up to an update frame before the draw, and the draw starts a render margin before a tear line that itself
    /// moves between presents: hit objects step unevenly from one present to the next.
    ///
    /// The clock runs at real time, shifted by the render margin of the present that will show the update frame, and holds still for a short window
    /// before that present's draw wakes. The frame the draw picks up is almost always sampled inside that window, so its clock reads exactly the
    /// present's tear time less a constant, whatever point of the update frame the draw caught. The window is the 99th percentile of how long before the
    /// wake drawn frames were sampled. Update frames that run once a draw has woken belong to the next present, which the draw thread foresees as it plans.
    ///
    /// The constant is steered so drawn scenes are on average exactly as old as without the timing. A first version averaged the shift over every update
    /// frame instead, which kept judgements exactly where they were, but most update frames are never drawn and were timed well ahead of the ones that are,
    /// so every drawn scene came out 1.9 ms older, and play felt it: the cursor seemed slow against the playfield. Centred on drawn frames, judgements move
    /// by however far the clock runs ahead on average, which the log reports. <c>OSU_RASTER_SCENE_CENTRE=all</c> goes back to centring on every update frame.
    /// </remarks>
    internal sealed class SceneTiming
    {
        /// <summary>
        /// Off with <c>OSU_RASTER_SCENE_TIMING=0</c>, which runs the gameplay clock at the time each update frame runs.
        /// </summary>
        public static readonly bool ENABLED = Environment.GetEnvironmentVariable(@"OSU_RASTER_SCENE_TIMING") != @"0";

        /// <summary>
        /// Whether the clock's shift is centred on the update frames that are drawn, keeping drawn scenes as old as they would be untimed, rather than on
        /// every update frame, keeping judgements where they would be.
        /// </summary>
        public static readonly bool CENTRE_ON_DRAWN = Environment.GetEnvironmentVariable(@"OSU_RASTER_SCENE_CENTRE") != @"all";

        /// <summary>
        /// While this file exists the gameplay clock runs untimed, so the timing can be switched mid-session for a blind comparison: <c>$XDG_RUNTIME_DIR/osu-scene-timing-off</c>.
        /// </summary>
        private static readonly string? switch_path = Environment.GetEnvironmentVariable(@"XDG_RUNTIME_DIR") is string runtimeDir ? System.IO.Path.Combine(runtimeDir, @"osu-scene-timing-off") : null;

        private const long switch_check_ns = 500_000_000;

        private long switchCheckedAt;
        private volatile bool switchedOff;

        /// <summary>
        /// Whether the live switch has the timing off. Read for the log.
        /// </summary>
        public bool SwitchedOff => switchedOff;

        /// <summary>
        /// A plan older than this is from before pacing stopped.
        /// </summary>
        private const long stale_plan_ns = 50_000_000;

        /// <summary>
        /// The furthest the clock is moved from real time either way, which bounds what a bad plan can do to judgements.
        /// </summary>
        private const long max_lead_ns = 4_000_000;

        /// <summary>
        /// The share of drawn frames the hold before each wake is sized to cover.
        /// </summary>
        /// <remarks>
        /// A step between two presents only comes out exact when both drawn frames were sampled inside the hold, so a hold covering 90% of them left
        /// about a fifth of steps untimed: measured in play, the median step error went from 0.110 to 0.010 ms but the 90th percentile only from 0.270 to 0.250.
        /// A longer hold stills the clock for longer before each draw, which moves judgements a little further on average.
        /// </remarks>
        private const double hold_percentile = 0.99;

        /// <summary>
        /// Draws between resizing the hold.
        /// </summary>
        private const int hold_interval = 64;

        /// <summary>
        /// How many samples the running means are taken over.
        /// </summary>
        private const double mean_frames = 1024;

        // Written on the draw thread, read on the update thread, as a sequence: odd while being written.
        private int planSequence;
        private long planWake;
        private long planTear;
        private long nextWake;
        private long nextTear;

        // Written on the draw thread, read on the update thread.
        private long holdNs = 300_000;
        private long centreNs = long.MinValue;

        // Written on the update thread, read on the draw thread: the sample time and shift of the newest published update frame.
        private const int slot_count = 4;
        private readonly long[] slotSampledAt = new long[slot_count];
        private readonly long[] slotLead = new long[slot_count];
        private readonly long[] slotRaw = new long[slot_count];
        private long published;

        // Update thread only.
        private double meanRawNs = double.NaN;
        private double publishDelayNs;
        private long sampledAt;
        private long frameLead;
        private long frameRaw;
        private long lastSceneNs;

        // Draw thread only.
        private readonly DurationWindow drawnPhases = new DurationWindow(1024);
        private int drawsSinceHold;
        private double meanDrawnRawNs = double.NaN;

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

            long lead = 0;
            long raw = 0;

            if (switch_path != null && now - switchCheckedAt > switch_check_ns)
            {
                switchCheckedAt = now;
                switchedOff = System.IO.File.Exists(switch_path);
            }

            if (!switchedOff && wake != 0 && now - wake < stale_plan_ns)
            {
                // A draw shows the newest scene published before it wakes.
                bool planned = now + (long)publishDelayNs < wake;
                long showWake = planned ? wake : followingWake;
                long showTear = planned ? tear : followingTear;

                // Real time plus that present's margin, held still for the window before its draw wakes.
                raw = Math.Min(now, showWake - Volatile.Read(ref holdNs)) + (showTear - showWake) - now;

                meanRawNs = double.IsNaN(meanRawNs) ? raw : meanRawNs + (raw - meanRawNs) / mean_frames;

                long centre = CENTRE_ON_DRAWN ? Volatile.Read(ref centreNs) : (long)meanRawNs;

                if (centre == long.MinValue)
                    centre = (long)meanRawNs;

                lead = Math.Clamp(raw - centre, -max_lead_ns, max_lead_ns);
            }

            // Never step the scene back.
            if (lastSceneNs != 0 && now + lead < lastSceneNs)
                lead = lastSceneNs - now;

            lastSceneNs = now + lead;
            sampledAt = now;
            frameLead = lead;
            frameRaw = raw;

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

            long end = Native.MonotonicNs();
            long delay = end - sampledAt;

            if (delay < stale_plan_ns)
                publishDelayNs += (delay - publishDelayNs) / mean_frames;

            long frame = published + 1;

            slotSampledAt[frame & (slot_count - 1)] = sampledAt;
            slotLead[frame & (slot_count - 1)] = frameLead;
            slotRaw[frame & (slot_count - 1)] = frameRaw;
            Volatile.Write(ref published, frame);

            sampledAt = 0;
        }

        /// <summary>
        /// A draw has woken for the present planned last, and picks up the newest published scene. Draw thread.
        /// </summary>
        public void NoteDraw(long wake, long tear)
        {
            long frame = Volatile.Read(ref published);

            // Untimed frames would pull the centre towards no shift, leaving the first seconds after switching back on off centre.
            if (frame == 0 || switchedOff)
                return;

            long sampled = slotSampledAt[frame & (slot_count - 1)];
            long lead = slotLead[frame & (slot_count - 1)];
            long raw = slotRaw[frame & (slot_count - 1)];

            // Overwritten while it was read.
            if (Volatile.Read(ref published) - frame >= slot_count - 1 || sampled == 0)
                return;

            drawnPhases.Add(Math.Max(0, wake - sampled));

            if (++drawsSinceHold >= hold_interval)
            {
                drawsSinceHold = 0;
                Volatile.Write(ref holdNs, drawnPhases.Percentile(hold_percentile));
            }

            // Centring on the drawn frames' mean shift keeps drawn scenes, on average, exactly as old as untimed.
            meanDrawnRawNs = double.IsNaN(meanDrawnRawNs) ? raw : meanDrawnRawNs + (raw - meanDrawnRawNs) / mean_frames;

            if (CENTRE_ON_DRAWN)
                Volatile.Write(ref centreNs, (long)meanDrawnRawNs);

#if RASTER_METRICS
            noteStep(wake, tear, sampled, sampled + lead);
#endif
        }

#if RASTER_METRICS
        // Draw thread: each drawn scene's time step against the step between tear lines, as sampled and as timed.
        private readonly DurationWindow sampledSteps = new DurationWindow(1024);
        private readonly DurationWindow timedSteps = new DurationWindow(1024);
        private long lastTear;
        private long lastSampled;
        private long lastTimed;
        private long lastWake;

        private void noteStep(long wake, long tear, long sampled, long timed)
        {
            // Only consecutive presents, not the first after pacing starts or after a stall.
            if (lastTear != 0 && tear > lastTear && wake - lastWake < stale_plan_ns / 5)
            {
                long tearStep = tear - lastTear;

                sampledSteps.Add(Math.Abs(sampled - lastSampled - tearStep));
                timedSteps.Add(Math.Abs(timed - lastTimed - tearStep));
            }

            lastTear = tear;
            lastSampled = sampled;
            lastTimed = timed;
            lastWake = wake;
        }

        /// <summary>
        /// The scene timing's part of a log line. Draw thread.
        /// </summary>
        public string Summary() =>
            $" Scene steps against tear line steps, ms off at p50/p90/p99: sampled {ms(sampledSteps.Percentile(0.5))}/{ms(sampledSteps.Percentile(0.9))}/{ms(sampledSteps.Percentile(0.99))}, "
            + $"timed {ms(timedSteps.Percentile(0.5))}/{ms(timedSteps.Percentile(0.9))}/{ms(timedSteps.Percentile(0.99))}. "
            + $"Live switch {(switchedOff ? "OFF" : "on")}. Held {ms(Volatile.Read(ref holdNs))} ms before each wake, centred on {(CENTRE_ON_DRAWN ? "drawn frames" : "every update frame")}, "
            + $"running the clock {(meanRawNs - (CENTRE_ON_DRAWN ? Volatile.Read(ref centreNs) : meanRawNs)) / 1e6:+0.000;-0.000} ms ahead on average, which is how far judgements move.";

        private static string ms(long ns) => $"{ns / 1e6:0.000}";
#endif
    }
}

// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Times update frames so the one a present draws finishes just before the draw thread wakes for it, and measures how old the scene a present draws is.
    /// </summary>
    /// <remarks>
    /// With the frame limiter lifted the update thread runs flat out, and nothing lines its frames up with the draw thread's wake.
    /// A draw takes whichever scene finished last, which by then has been waiting for anything up to a whole update frame,
    /// on top of the update frame it took to build. A frame shows input and time as they were when its update frame started,
    /// so that wait is latency just as the render margin is.
    ///
    /// The update thread's clock is sampled after <c>UpdateFrame</c> returns, when <c>GameThread</c> processes its clock, and input is
    /// collected inside the next one. The wait therefore goes at the end of <c>UpdateFrame</c>: time and input are both read after it.
    /// </remarks>
    /// <remarks>
    /// Off by default. Measured on one map, timing update frames made scenes older rather than newer: 1.61 ms at present against 1.26 ms
    /// running free. Frames were aimed to finish in time at their slowest percent, so the typical one finished half a millisecond early,
    /// and frames woken from a sleep ran slower. Running free, a scene only sat 0.17 ms before the draw woke, which is all this could ever recover.
    /// </remarks>
    internal sealed class UpdateSync
    {
        public enum SyncMode
        {
            /// <summary>
            /// Update frames run flat out, as they did before. Only the measurement runs.
            /// </summary>
            Off,

            /// <summary>
            /// One update frame per present, timed to finish just before the draw thread wakes.
            /// </summary>
            Aligned,

            /// <summary>
            /// As <see cref="Aligned"/>, but frames that would finish before the timed one starts are run too,
            /// so input and hits are still processed as often as update frames allow.
            /// </summary>
            Fill,
        }

        public static readonly SyncMode MODE = Environment.GetEnvironmentVariable(@"OSU_RASTER_UPDATE_SYNC")?.ToLowerInvariant() switch
        {
            @"fill" => SyncMode.Fill,
            @"aligned" => SyncMode.Aligned,
            _ => SyncMode.Off,
        };

        /// <summary>
        /// The fraction of recent update frames the timed one is expected to finish within. One that overruns leaves the draw thread waiting for it.
        /// </summary>
        private const double update_percentile = 0.99;

        /// <summary>
        /// Time left between an update frame finishing and the draw thread waking for it.
        /// </summary>
        private const long guard_ns = 50_000;

        /// <summary>
        /// How often the update thread refreshes its estimate of its own frame time, since reading a percentile scans the window.
        /// </summary>
        private const int estimate_interval = 64;

        /// <summary>
        /// A plan older than this belongs to a draw thread that has stopped presenting, so update frames stop waiting on it.
        /// </summary>
        private const long stale_plan_ns = 50_000_000;

        private const int history = 1024;

        // Written on the draw thread, read on the update thread.
        private long planWake;
        private long planSlice;

#if RASTER_METRICS
        // Written on the update thread, read on the draw thread. A published frame's times sit in the slot its number picks,
        // and stay readable until four more frames have been published.
        private const int slot_count = 4;
        private readonly long[] frameStarts = new long[slot_count];
        private readonly long[] frameEnds = new long[slot_count];
        private long publishedFrame;
#endif
        private int updatesAligned;
        private int updatesFilled;
        private long estimateNs;

        // Update thread only.
        private readonly DurationWindow updateFrames = new DurationWindow(history);
        private readonly DurationWindow sleepOvershoots = new DurationWindow(history);
        private long frameStart;
        private int framesSinceEstimate;
        private bool timerSlackSet;

#if RASTER_METRICS
        // Draw thread only.
        private readonly DurationWindow drawnUpdateFrames = new DurationWindow(history);
        private readonly DurationWindow agesAtDraw = new DurationWindow(history);
        private readonly DurationWindow agesAtPresent = new DurationWindow(history);
        private readonly DurationWindow idleBeforeDraw = new DurationWindow(history);
        private readonly DurationWindow waitsForUpdate = new DurationWindow(history);
        private long frameAtWake;
        private long wakeNs;
        private long lastDrawnFrame = -1;
        private long drawnFrameStart;
        private bool drawnFrameKnown;
        private long intervalStartFrame;
        private int intervalDraws;
        private int intervalWaited;
        private long intervalUpdatesPerDraw;
        private long intervalMaxAgeAtPresent;
#endif

        /// <summary>
        /// Update thread, at the end of <c>UpdateFrame</c> once the scene it built has been published.
        /// </summary>
        /// <param name="mayWait">Whether this thread is only updating. With a single thread, waiting here would hold up the draw too.</param>
        public void FinishUpdateFrame(bool mayWait)
        {
#if !RASTER_METRICS
            if (MODE == SyncMode.Off)
                return;
#endif

            long end = Native.MonotonicNs();

            if (frameStart > 0)
            {
#if RASTER_METRICS
                long frame = publishedFrame + 1;

                frameStarts[frame & (slot_count - 1)] = frameStart;
                frameEnds[frame & (slot_count - 1)] = end;
                Volatile.Write(ref publishedFrame, frame);
#endif

                updateFrames.Add(end - frameStart);

                if (++framesSinceEstimate >= estimate_interval)
                {
                    framesSinceEstimate = 0;
                    Volatile.Write(ref estimateNs, updateFrames.Percentile(update_percentile) + sleepOvershoots.Percentile(update_percentile));
                }
            }

            frameStart = mayWait && MODE != SyncMode.Off ? waitForTimedStart(end) : end;
        }

        /// <summary>
        /// Sleeps until the next update frame has to start to finish just before the draw thread wakes, and returns when it did start.
        /// </summary>
        private long waitForTimedStart(long now)
        {
            long wake = Volatile.Read(ref planWake);
            long slice = Volatile.Read(ref planSlice);

            if (wake == 0 || slice <= 0 || now - wake > stale_plan_ns)
                return now;

            if (!timerSlackSet)
            {
                Native.SetTimerSlack(1);
                timerSlackSet = true;
            }

            long estimate = Volatile.Read(ref estimateNs);
            long start = wake - estimate - guard_ns;

            // Presents after the planned one are expected a slice apart. A start that has passed belongs to a draw that
            // this frame's scene may already be ready for, so the next wake is aimed at.
            if (start < now)
                start += (now - start + slice - 1) / slice * slice;

            if (MODE == SyncMode.Fill && start - now >= estimate + guard_ns)
            {
                Interlocked.Increment(ref updatesFilled);
                return now;
            }

            Native.SleepUntil(start);

            long woke = Native.MonotonicNs();

            sleepOvershoots.Add(Math.Max(0, woke - start));
            Interlocked.Increment(ref updatesAligned);
            return woke;
        }

        /// <summary>
        /// Draw thread, once a present is planned.
        /// </summary>
        public void NotePlan(long wake, long slice)
        {
            Volatile.Write(ref planSlice, slice);
            Volatile.Write(ref planWake, wake);
        }

        /// <summary>
        /// Draw thread, whenever presents stop being planned.
        /// </summary>
        public void ClearPlan()
        {
            Volatile.Write(ref planWake, 0);
#if RASTER_METRICS
            lastDrawnFrame = -1;
            drawnFrameKnown = false;
#endif
        }

#if RASTER_METRICS
        /// <summary>
        /// Draw thread, on waking to draw. The draw takes the newest scene published by then, or waits for the next if it has drawn that one already.
        /// </summary>
        public void NoteWake(long wake)
        {
            wakeNs = wake;
            frameAtWake = Volatile.Read(ref publishedFrame);
            drawnFrameKnown = false;
        }

        /// <summary>
        /// Draw thread, once the frame has been drawn, when the scene it drew has certainly been published.
        /// </summary>
        public void NoteDrawn()
        {
            if (frameAtWake == 0)
                return;

            long drawn = frameAtWake > lastDrawnFrame ? frameAtWake : lastDrawnFrame + 1;
            long published = Volatile.Read(ref publishedFrame);

            if (drawn > published || published - drawn >= slot_count)
                return;

            long start = frameStarts[drawn & (slot_count - 1)];
            long end = frameEnds[drawn & (slot_count - 1)];

            // Overwritten while it was read.
            if (Volatile.Read(ref publishedFrame) - drawn >= slot_count || end < start)
                return;

            long drawStart = Math.Max(wakeNs, end);

            drawnUpdateFrames.Add(end - start);
            agesAtDraw.Add(drawStart - start);

            if (end > wakeNs)
            {
                waitsForUpdate.Add(end - wakeNs);
                intervalWaited++;
            }
            else
            {
                waitsForUpdate.Add(0);
                idleBeforeDraw.Add(wakeNs - end);
            }

            if (lastDrawnFrame >= 0)
                intervalUpdatesPerDraw += drawn - lastDrawnFrame;

            intervalDraws++;
            lastDrawnFrame = drawn;
            drawnFrameStart = start;
            drawnFrameKnown = true;
        }

        /// <summary>
        /// Draw thread, when a drawn frame's present starts.
        /// </summary>
        public void NotePresent(long presentStart)
        {
            if (!drawnFrameKnown)
                return;

            long age = presentStart - drawnFrameStart;

            agesAtPresent.Add(age);
            intervalMaxAgeAtPresent = Math.Max(intervalMaxAgeAtPresent, age);
        }

        /// <summary>
        /// A log line for the interval just ended, which starts the next. Draw thread.
        /// </summary>
        public string TakeIntervalSummary(long intervalNs)
        {
            long published = Volatile.Read(ref publishedFrame);
            double updatesPerSecond = (published - intervalStartFrame) * 1e9 / intervalNs;
            double updatesPerDraw = intervalDraws > 1 ? (double)intervalUpdatesPerDraw / (intervalDraws - 1) : 0;
            int aligned = Interlocked.Exchange(ref updatesAligned, 0);
            int filled = Interlocked.Exchange(ref updatesFilled, 0);

            string summary = $"Raster sync update: mode {MODE}, {updatesPerSecond:0} update frames/s, {updatesPerDraw:0.00} per present, {aligned} timed and {filled} run early. "
                             + $"Milliseconds at p50/p99/max: scene age at present {ms(agesAtPresent.Percentile(0.5))}/{ms(agesAtPresent.Percentile(0.99))}/{ms(intervalMaxAgeAtPresent)} "
                             + $"= age at draw start {ms(agesAtDraw.Percentile(0.5))}/{ms(agesAtDraw.Percentile(0.99))} + the render margin. "
                             + $"Drawn update frames took {ms(drawnUpdateFrames.Percentile(0.5))}/{ms(drawnUpdateFrames.Percentile(0.99))}, "
                             + $"then sat {ms(idleBeforeDraw.Percentile(0.5))}/{ms(idleBeforeDraw.Percentile(0.99))} before the draw woke. "
                             + $"{intervalWaited} of {intervalDraws} draws waited for their update frame, p99 {ms(waitsForUpdate.Percentile(0.99))}. "
                             + $"Timed update frames aim to finish {ms(Volatile.Read(ref estimateNs))} ms after starting.";

            intervalStartFrame = published;
            intervalDraws = 0;
            intervalWaited = 0;
            intervalUpdatesPerDraw = 0;
            intervalMaxAgeAtPresent = 0;

            return summary;
        }

        private static string ms(long ns) => $"{ns / 1e6:0.000}";
#endif
    }
}

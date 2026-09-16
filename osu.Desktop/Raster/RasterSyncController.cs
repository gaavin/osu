// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.Threading;
using osu.Framework.Bindables;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Configuration;
using osu.Game.Graphics.Raster;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Times each present against the display's scanout during gameplay, so tear lines land where they are wanted rather than wherever a frame happens to finish.
    /// </summary>
    /// <remarks>
    /// This is Blur Busters' beam racing. With the swap interval at 0 and the compositor flipping asynchronously, a new frame takes over from
    /// whichever scanline is being scanned out at the moment of the flip. Each frame is drawn as late as its measured render time allows,
    /// finished on the GPU, then held until scanout reaches its target scanline.
    /// </remarks>
    internal sealed class RasterSyncController : IRasterSync, IDisposable
    {
        /// <summary>
        /// Presents whose render time counts towards the prediction for the next one.
        /// </summary>
        private const int cost_history = 1024;

        /// <summary>
        /// The fraction of recent presents the prediction covers before it has watched any finish late.
        /// </summary>
        private const double cost_percentile_initial = 0.99;

        /// <summary>
        /// The share of presents allowed to finish after their scanline. Frames are drawn as late as that allows, so the scene they show is as new as possible.
        /// </summary>
        /// <remarks>
        /// This sets the margin a frame starts on, which is the half of frame age that the slice count does not decide:
        /// measured in play, 0.79 ms of margin against a 1.09 ms gap between presents. Allowing more frames to finish
        /// late pushes the prediction down and the margin with it, and pays for it in tear lines that land past the
        /// scanline they were aimed at, so it can be set for a play.
        /// </remarks>
        private static readonly double late_target = envFraction(@"OSU_RASTER_LATE_TARGET", 0.02);

        private const double cost_percentile_step_up = 0.01;
        private const double cost_percentile_step_down = 0.005;
        /// <summary>
        /// How far down the prediction may be steered. Well clear of where it settles at the default target, which is
        /// around 99%, so this only comes into play when many more frames are allowed to finish late.
        /// </summary>
        private const double cost_percentile_min = 0.5;
        private const double cost_percentile_max = 0.999;

        /// <summary>
        /// Presents an interval needs before its late presents move the prediction.
        /// </summary>
        private const int cost_adjust_min_presents = 100;

        /// <summary>
        /// The fraction of recent presents covered by the predictions that are not steered by how many finish late.
        /// </summary>
        private const double fixed_percentile = 0.99;

        /// <summary>
        /// The fraction of recent presents a slice count has to fit inside a slice, which is stricter than the margin frames start on.
        /// A frame that overruns the margin only starts late, but one that overruns its slice also leaves the next slice without a frame of its own.
        /// </summary>
        /// <remarks>
        /// This is what limits the count, rather than how quickly it climbs. Measured on the same map, judging the count
        /// on all but the slowest 1% of frames rather than the slowest 0.1% took it from 3.7 slices to 6.3, and the gap
        /// between presents — which is how old a frame is when it reaches the screen — from 1.88 ms to 1.11 ms.
        /// It is paid for in slices that go without a frame of their own, which rose from 0.3 a second to 7.2, though
        /// that is still under one slice in a hundred, and the play it was measured on felt better rather than worse.
        /// </remarks>
        private static readonly double slice_fit_percentile = envFraction(@"OSU_RASTER_SLICE_FIT", 0.99);

        private static double envFraction(string name, double fallback)
        {
            return double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                   && value > 0
                   && value < 1
                ? value
                : fallback;
        }

        /// <summary>
        /// A looser rule, logged beside the one in force but never acted on, to say what relaxing it would allow.
        /// </summary>
        private const double slice_fit_loose = 0.99;

        /// <summary>
        /// One in how many frames has its processor time read. Reading it is a system call, which a frame pays for whether or not it is sampled.
        /// </summary>
        private const int cpu_sample_interval = 16;

        /// <summary>
        /// How long more frame slices have to keep fitting before refreshes are split into them, so tear lines don't hop between counts.
        /// </summary>
        /// <remarks>
        /// Measured in play, the count in use sat a whole slice below what the frame times allowed for half of every
        /// second, so this is shorter than the two seconds it began as. Both this and <see cref="slice_fit_grace_ns"/>
        /// can be set for a play, since what they cost is latency and what they buy is steady tear lines.
        /// </remarks>
        private static readonly long slice_raise_delay_ns = envMilliseconds(@"OSU_RASTER_SLICE_RAISE_MS", 1_000);

        /// <summary>
        /// How long the higher count has to stop fitting before the wait for it starts over.
        /// </summary>
        /// <remarks>
        /// Without this, one present that failed to qualify threw away the whole wait, so a higher count had to fit on
        /// every one of the roughly nine hundred presents in two seconds without a single miss. A dip now only pauses it.
        /// </remarks>
        private static readonly long slice_fit_grace_ns = envMilliseconds(@"OSU_RASTER_SLICE_GRACE_MS", 250);

        private static long envMilliseconds(string name, long fallback)
        {
            long ms = long.TryParse(Environment.GetEnvironmentVariable(name), out long parsed) && parsed >= 0 ? parsed : fallback;

            return ms * 1_000_000;
        }

        /// <summary>
        /// How much of a slice a frame has to fit inside before more slices are used. A count that only just fits would be dropped again by the next slow frame.
        /// </summary>
        private const double slice_raise_fit = 0.9;

        /// <summary>
        /// How long before a present the draw thread stops sleeping and spins instead.
        /// </summary>
        private const long spin_window_ns = 1_000_000;

        /// <summary>
        /// Presents between flips timed during a play, to keep the probe's polling to a fraction of a core.
        /// </summary>
        private const int probe_interval = 3;

        /// <summary>
        /// Scanlines the tear line moves by per present while steering, so refits glide it rather than jump it.
        /// </summary>
        private const double max_steer_lines = 1;

        /// <summary>
        /// How often the status note is refreshed, and a line logged, while presents are timed.
        /// </summary>
        private const long status_interval_ns = 1_000_000_000;

        private readonly RasterSyncLinuxGameHost host;

        private Bindable<RasterSyncMode> modeSetting = null!;
        private Bindable<int> slicesSetting = null!;
        private Bindable<double> headroomSetting = null!;
        private IBindable<DisplayMode>? displayMode;

        private TearlineOffsetFinder? finder;

        // Written on the update thread, read on the draw thread.
        private volatile RasterSyncMode mode;
        private volatile int slices = 1;
        private volatile bool playing;
        private long headroomNs;
        private volatile DrmVBlankClock? clock;
        private volatile DrmVBlankClock.DisplayHint? displayHint;

        private long presentCount;
        private volatile string status = "Off";

        // Draw thread only.

        /// <summary>
        /// From the draw thread waking to the frame being finished on the GPU: the draw, then the wait for the GPU.
        /// </summary>
        private readonly DurationWindow renderCosts = new DurationWindow(cost_history);

#if RASTER_METRICS
        /// <summary>
        /// The draw thread's own work on a frame, up to the point the GPU is asked to finish it.
        /// </summary>
        private readonly DurationWindow draws = new DurationWindow(cost_history);

        /// <summary>
        /// The processor time of the same work, sampled every <see cref="cpu_sample_interval"/> frames.
        /// What the wall clock has on top of it was spent suspended, waiting or preempted.
        /// </summary>
        private readonly DurationWindow drawCpuTimes = new DurationWindow(cost_history);

        /// <summary>
        /// Draws a garbage collection ran during, kept apart from <see cref="drawsWithoutCollection"/> to say how much of the draw tail is collections.
        /// </summary>
        private readonly DurationWindow drawsWithCollection = new DurationWindow(cost_history);

        private readonly DurationWindow drawsWithoutCollection = new DurationWindow(cost_history);

        /// <summary>
        /// Waiting for the GPU to finish a frame the draw thread has already submitted.
        /// </summary>
        private readonly DurationWindow gpuFinishes = new DurationWindow(cost_history);

        /// <summary>
        /// How long past the moment a frame had to start drawing the draw thread actually woke, which eats the same margin as a slow frame.
        /// </summary>
        private readonly DurationWindow wakeOvershoots = new DurationWindow(cost_history);
#endif

        /// <summary>
        /// From starting one timed swap to planning the next present: the swap call, then the rest of the frame loop.
        /// </summary>
        private readonly DurationWindow betweenPresents = new DurationWindow(cost_history);

#if RASTER_METRICS
        private readonly DurationWindow swapCalls = new DurationWindow(cost_history);

        /// <summary>
        /// From a timed swap returning to planning the next present.
        /// </summary>
        private readonly DurationWindow frameLoops = new DurationWindow(cost_history);

        /// <summary>
        /// How late presents that waited for their scanline started, which is how far their tear lines land past the one they were aimed at.
        /// </summary>
        private readonly DurationWindow timingErrors = new DurationWindow(cost_history);

        /// <summary>
        /// What the runtime says each ephemeral collection cost, sampled once per collection rather than once per present.
        /// </summary>
        private readonly DurationWindow gcPauses = new DurationWindow(cost_history);
#endif

        /// <summary>
        /// Holds collections until a present has set time aside for one.
        /// </summary>
        private readonly GcPacer gcPacer = new GcPacer();

        /// <summary>
        /// Times update frames against the draw thread's wake, and measures how old the scene each present draws is.
        /// </summary>
        public readonly UpdateSync UpdateSync = new UpdateSync();

        /// <summary>
        /// Whether a cursor drawn after the rest of the frame is waited on to finish on the GPU before the present. Off unless
        /// <c>OSU_POINTER_LATCH_WAIT_GPU=1</c>: the cursor is a few quads, and the compositor waits for the GPU before flipping anyway.
        /// </summary>
        /// <remarks>
        /// Measured in play with the wait, the held draw took 0.12 ms at p50 and 0.39 ms at p99. Planning every frame around that
        /// dropped a slice, which aged the rest of the screen by 0.4 ms against the 0.33 ms it saved the cursor.
        /// </remarks>
        private static readonly bool late_draw_waits_for_gpu = Environment.GetEnvironmentVariable(@"OSU_POINTER_LATCH_WAIT_GPU") == @"1";

        /// <summary>
        /// From starting a cursor draw held until the rest of the frame had finished on the GPU, to it being submitted, or finished on the GPU if that is waited for.
        /// </summary>
        private readonly DurationWindow lateDraws = new DurationWindow(cost_history);

#if RASTER_METRICS
        /// <summary>
        /// The part of <see cref="lateDraws"/> spent drawing and submitting, before any wait for the GPU.
        /// </summary>
        private readonly DurationWindow lateDrawSubmits = new DurationWindow(cost_history);
#endif

        /// <summary>
        /// Scanlines between the tear line given to the cursor and the top of the cursor, to cover the tear line landing off target,
        /// and the pen moving up between planning the present and drawing the cursor. <c>OSU_CURSOR_TEARLINE_LEAD</c> sets it.
        /// </summary>
        private static readonly int cursor_tearline_lead = int.TryParse(Environment.GetEnvironmentVariable(@"OSU_CURSOR_TEARLINE_LEAD"), out int lead) && lead >= 0 ? lead : 32;

        /// <summary>
        /// How many evenly spaced positions a refresh offers the cursor's tear line, counted from the blanking interval's, or 0 to follow the cursor
        /// to the scanline. <c>OSU_CURSOR_TEARLINE_BANDS</c> sets it.
        /// </summary>
        /// <remarks>
        /// The cursor's present is timed by the cursor's height, so the frame drawn below it moves in time with it: a jump across half the screen
        /// shifts that frame by half a refresh, and everything under the cursor advances unevenly. Holding the tear line to a few positions only
        /// shifts it when the cursor crosses into another band, by one band, at the cost of the cursor sitting up to a band below its tear line.
        /// </remarks>
        private static readonly int cursor_tearline_bands = int.TryParse(Environment.GetEnvironmentVariable(@"OSU_CURSOR_TEARLINE_BANDS"), out int bands) && bands >= 0 ? bands : 0;

        private bool plannedForCursor;
        private bool plannedMerged;
        private double plannedCursorLine;

        private double plannedOffset;

#if RASTER_METRICS
        private double lastCursorLine = double.NaN;

        /// <summary>
        /// How evenly the scene advances in each band of the screen, refresh to refresh, which is what frame pacing looks like with several presents a refresh.
        /// </summary>
        private readonly PacingMonitor pacing = new PacingMonitor();
#endif
        private long lastGridTarget;
        private long lastCursorTarget;

#if RASTER_METRICS
        // What the slice count was last decided on. Logged every second, since a count that keeps falling back is
        // otherwise only visible at the moment it changes.
        private long lastSlowFrameNs;
        private int lastFits;
#endif

        private double costPercentile = cost_percentile_initial;
        private bool betweenPending;
        private int sliceCount = 1;
        private long moreSlicesFitSince;
        private long moreSlicesFailedAt;
        private bool planned;
        private DrmVBlankClock.Timing? plannedTiming;
        private long plannedTarget;
        private int? plannedSlice;
        private long plannedWake;
        private long lastTarget;
        private double? steeredOffset;
        private long wakeTime;
#if RASTER_METRICS
        private long wakeCpuTime;
        private int planCollections;
        private int wakeCollections;
        private int cpuSampleCounter;
        private bool samplingCpu;
        private long drawEnd;
        private long drawEndCpuTime;
        private int drawEndCollections;
        private int readyCollections;
        private int presentCollections;
        private long swapEnd;
#endif
        private long presentStart;
        private bool timerSlackSet;
        private string? blockedBy = "Off";

        private FlipProbe? probe;
        private bool probeArmed;
        private int presentsSinceProbe;

        private long intervalStart;
        private int intervalPresents;
        private int intervalLate;
        private int intervalSkipped;

#if RASTER_METRICS
        /// <summary>
        /// Slices deliberately given up so that a collection had somewhere to run, which are not uneven pacing.
        /// </summary>
        private int intervalCollectionSkips;

        private int intervalDrawsWithCollection;
        private long intervalMaxDrawWithCollection;

        // Where in a present the runtime's collections land. Only the ones during the sleep are free.
        private int intervalCollectionsIdle;
        private int intervalCollectionsDrawing;
        private int intervalCollectionsGpu;
        private int intervalCollectionsWaiting;
        private int intervalCollectionsSwapping;

#endif

        private int lastCollections;
        private long gcIndex;
        private long gcAllocated;

#if RASTER_METRICS
        private long intervalGcBytes;
        private int intervalGcSamples;

        // The pauses of this second's collections. Percentiles over recent collections reach back minutes once
        // collections are rare, so what a second actually paid for has to be counted within the second.
        private int intervalPauses;
        private long intervalPauseTotal;
        private long intervalPauseMax;

#endif

        /// <summary>
        /// The most recent gap between collections, reported in the seconds that saw none of their own.
        /// </summary>
        private long lastGcBytes;

#if RASTER_METRICS
        private long intervalMaxCost;
        private long intervalMaxDraw;
        private long intervalMaxFinish;
        private long intervalMaxWake;
        private long intervalMaxSwap;
        private long intervalMaxBetween;
        private long intervalMaxError;
        private int intervalGen0;
        private int intervalGen1;
        private int intervalGen2;
        private int intervalLateDraws;
        private int intervalCursorPresents;
        private int intervalCursorSkips;
        private int intervalCursorOnlyRefreshes;
        private int intervalMergedPresents;
        private double intervalCursorLineMoves;
        private double intervalMaxCursorLineMove;
        private long intervalMaxLateDraw;
#endif

        // Counted on the probe thread, read and reset on the draw thread.
        private int intervalFlips;
        private int intervalOvertaken;

        public long PresentCount => Interlocked.Read(ref presentCount);

        public int? PlannedSlice => planned ? plannedSlice : null;

        public string Status => status;

        public string TearlineSteeringStatus => finder?.Describe(clock?.Current) ?? @"Off";

        public void ForgetRecordedFlips() => finder?.Forget();

        public RasterSyncController(RasterSyncLinuxGameHost host)
        {
            this.host = host;
        }

        /// <summary>
        /// Starts following the settings. Update thread.
        /// </summary>
        public void BindTo(OsuConfigManager config)
        {
            finder = new TearlineOffsetFinder(host.Storage);

            slicesSetting = config.GetBindable<int>(OsuSetting.RasterFrameSlices);
            headroomSetting = config.GetBindable<double>(OsuSetting.RasterRenderHeadroom);
            modeSetting = config.GetBindable<RasterSyncMode>(OsuSetting.RasterSyncMode);

            slicesSetting.BindValueChanged(s => slices = s.NewValue, true);
            headroomSetting.BindValueChanged(h => Interlocked.Exchange(ref headroomNs, (long)(h.NewValue * 1_000_000)), true);

            if (host.Window != null)
            {
                displayMode = host.Window.CurrentDisplayMode.GetBoundCopy();
                displayMode.BindValueChanged(m => displayHint = new DrmVBlankClock.DisplayHint(m.NewValue.Size.Width, m.NewValue.Size.Height, m.NewValue.RefreshRate), true);
            }

            modeSetting.BindValueChanged(m =>
            {
                mode = m.NewValue;

                // The clock keeps following vblanks in menus, so pacing starts on a settled fit when gameplay does.
                bool enabled = mode != RasterSyncMode.Disabled;

                if (enabled && clock == null)
                    clock = new DrmVBlankClock(() => displayHint);
                else if (!enabled && clock != null)
                {
                    clock.Dispose();
                    clock = null;
                }

                updateFrameLimits();
            }, true);
        }

        /// <summary>
        /// Paces presents, and records flips to steer the tear line with, only while the user plays.
        /// Menus draw far more than a frame per refresh and gain nothing from waiting on scanout, so they are left to draw as they otherwise would.
        /// Update thread.
        /// </summary>
        public void SetPlaying(bool isPlaying)
        {
            if (playing == isPlaying)
                return;

            playing = isPlaying;

            if (isPlaying)
                finder?.BeginPlay();
            else
                finder?.EndPlay();

            updateFrameLimits();
        }

        private void updateFrameLimits() => host.SetUnlimitedFrames(mode != RasterSyncMode.Disabled && playing);

        /// <summary>
        /// Whether to time this frame's present. Draw thread.
        /// </summary>
        /// <param name="blocker">Why the host cannot race the beam at the moment, or null if it can.</param>
        public bool ShouldPace(string? blocker)
        {
            var currentClock = clock;

            if (mode == RasterSyncMode.Disabled || currentClock == null)
                blocker = @"Off";
            else if (!playing)
                blocker = @"Waiting for gameplay";
            else if (blocker == null && currentClock.Current == null)
                blocker = currentClock.Status;

            if (blocker != null)
            {
                if (blocker != blockedBy)
                    status = blockedBy = blocker;

                planned = false;
                betweenPending = false;
                UpdateSync.ClearPlan();
                return false;
            }

            if (blockedBy != null)
            {
                blockedBy = null;
                status = currentClock!.Status;
                startInterval(Native.MonotonicNs());
            }

            return true;
        }

        public bool HasPlannedPresent => planned;

        /// <summary>
        /// Whether the planned present is the refresh's one tear line kept just above the cursor, rather than one of the evenly spaced slices. Draw thread.
        /// </summary>
        public bool PlannedForCursor => plannedForCursor;

        /// <summary>
        /// Picks the scanline the next present should tear at, then sleeps until the frame has to start. Draw thread.
        /// </summary>
        public void PlanNextPresent()
        {
            if (!timerSlackSet)
            {
                Native.SetTimerSlack(1);
                timerSlackSet = true;
            }

            var timing = clock?.Current;

            if (timing == null)
            {
                planned = false;
                UpdateSync.ClearPlan();
                return;
            }

            bool followsPresent = betweenPending;

            if (betweenPending)
            {
                long planStart = Native.MonotonicNs();

                betweenPresents.Add(planStart - presentStart);
#if RASTER_METRICS
                frameLoops.Add(planStart - swapEnd);
                intervalMaxBetween = Math.Max(intervalMaxBetween, planStart - presentStart);
#endif
                betweenPending = false;
            }

            long renderNs = renderCosts.Percentile(costPercentile);
            long headroom = Interlocked.Read(ref headroomNs);

            // A collection that is due is paid for by this present: adding it to the cost aims the present at a later
            // slice, which leaves the gap before the frame starts drawing long enough to collect in.
            long reserved = gcPacer.Reserve();
            long cost = renderNs + headroom + reserved;

            int previousCount = sliceCount;
            int count = 1;

            if (mode == RasterSyncMode.FrameSlices)
            {
                // Frames that overrun a slice leave the next one without a frame of its own, which shows as uneven pacing rather than latency,
                // so the count is decided on a stricter percentile than the margin a frame starts on.
                long slowFrameNs = renderCosts.Percentile(slice_fit_percentile) + headroom + betweenPresents.Percentile(slice_fit_percentile);

                count = chooseSliceCount(timing, slowFrameNs);

#if RASTER_METRICS
                lastSlowFrameNs = slowFrameNs;

                if (count != previousCount)
                    logSliceCountChange(timing, previousCount, count, slowFrameNs, headroom);
#endif
            }

            long slice = timing.PeriodNs / count;

            // The first tear line of each refresh aims at the middle of the blanking interval, moved by the offset.
            // Further slices follow at even spacing down the screen.
            double offset = steerOffset(timing);
            plannedOffset = offset;
            double tearline = (timing.VDisplay + timing.VTotal) / 2.0 + offset;
            long anchor = timing.VBlankNs + (long)(tearline * timing.PeriodNs / timing.VTotal);

            long now = Native.MonotonicNs();
            long target = anchor + ceilingDivide(now + cost - anchor, slice) * slice;

            // A frame that finished early would otherwise tear into the slice the previous frame went to. Measured from the last slice's
            // present rather than the cursor's, which tears between slices: with one tear line a refresh, half a refresh from the cursor's
            // present pushed the blanking interval's present back a whole refresh whenever the cursor was in the lower half of the screen.
            if (target - lastGridTarget < slice / 2 || target <= lastTarget)
                target += slice;

            bool forCursor = false;
            bool merged = false;

            // One more tear line each refresh sits just above the cursor, and its present draws the cursor last, so the cursor is scanned out
            // right after the newest pen report is taken. The evenly spaced slices stay where they are, bar any the cursor's present crowds out.
            // With one tear line a refresh, in the blanking interval, that makes two presents a refresh, which is the fewest that keeps both.
            if (PenLatch.LATE && host.PenLatch?.TryGetCursorTop(out float cursorTop) == true)
            {
                long lateNs = lateDraws.Percentile(costPercentile);
                double blankingLine = (timing.VDisplay + timing.VTotal) / 2.0;
                double line = cursorTop - cursor_tearline_lead;

                if (cursor_tearline_bands > 0)
                {
                    double band = (double)timing.VTotal / cursor_tearline_bands;

                    line = blankingLine + Math.Floor((line - blankingLine) / band) * band;
                }

                if (count == 1)
                {
                    // The blanking interval's present is never given up. Measured in play, dropping it whenever the cursor was too close to it for both
                    // left the top of the screen alternating between frames 1.75 ms apart, refresh to refresh. A cursor low on the screen has its tear
                    // line pulled up far enough for a whole frame to fit before the blanking interval's, and one high on the screen, which that tear
                    // line is only just above anyway, has its draw held on the blanking interval's present instead of a present of its own.
                    double gapLines = (double)(betweenPresents.Percentile(costPercentile) + cost + lateNs) * timing.VTotal / timing.PeriodNs;

                    line = Math.Min(line, blankingLine - gapLines);
                    merged = line < blankingLine - timing.VTotal + gapLines;

                    if (merged)
                        line = blankingLine - timing.VTotal;
                }

                plannedCursorLine = line;

                if (merged)
                {
                    target = anchor + ceilingDivide(now + cost + lateNs - anchor, slice) * slice;

                    if (target - lastGridTarget < slice / 2 || target <= lastTarget)
                        target += slice;

                    cost += lateNs;
                    forCursor = true;
                }
                else
                {
                    line += offset;
                    long cursorBase = timing.VBlankNs + (long)(line * timing.PeriodNs / timing.VTotal);
                    long cursorTarget = cursorBase + ceilingDivide(now + cost + lateNs - cursorBase, timing.PeriodNs) * timing.PeriodNs;

                    // Once a refresh.
                    if (cursorTarget - lastCursorTarget < timing.PeriodNs / 2)
                        cursorTarget += timing.PeriodNs;

                    // A slice presented first would have to leave time for another whole frame before the cursor's, or the cursor waits a refresh.
                    // With one tear line a refresh the cursor's has already been placed to leave that time, so whichever comes first goes first.
                    long afterSlice = count == 1 ? target : target + betweenPresents.Percentile(costPercentile) + cost + lateNs;

                    if (cursorTarget < afterSlice)
                    {
                        target = cursorTarget;
                        cost += lateNs;
                        forCursor = true;
                    }
                }
            }

            // Slices left without a frame of their own since the last present. A slice given up to hold a collection, or crowded out by
            // the cursor's present, is counted apart from those, since skipped slices are how uneven pacing shows and these were chosen.
            if ((!forCursor || merged) && followsPresent && count == previousCount && target - lastGridTarget > slice * 3 / 2)
            {
                int missed = (int)((target - lastGridTarget + slice / 2) / slice) - 1;

                if (lastTarget == lastCursorTarget)
                {
#if RASTER_METRICS
                    intervalCursorSkips += missed;
#endif
                }
                else if (reserved == 0)
                    intervalSkipped += missed;
#if RASTER_METRICS
                else
                    intervalCollectionSkips += missed;
#endif
            }

            // Targets are counted in slices from the anchor, whose slice is the one at the top of the screen. The cursor's present takes the
            // number of the slice it tears inside, so the tear line indicator keeps that slice's colour.
            long sliceNumber = (long)Math.Floor((double)(target - anchor) / slice);
            plannedSlice = count > 1 ? (int)((sliceNumber % count + count) % count) : null;
            plannedForCursor = forCursor;
            plannedMerged = merged;

            plannedTiming = timing;
            plannedTarget = target;
            plannedWake = target - cost;
            planned = true;

            if (UpdateSync.MODE != UpdateSync.SyncMode.Off)
                UpdateSync.NotePlan(plannedWake, slice);

#if RASTER_METRICS
            // A collection that runs while the draw thread sleeps costs it nothing: the thread is inside a blocking
            // call, so the runtime suspends it without waiting for it to reach a safe point. Counting those apart
            // from the rest says how much of the collection load is already free, and how much is in the way.
            planCollections = GC.CollectionCount(0);
#endif

            // Inside the sleep window as far as the counters are concerned, which is exactly where it should land.
            gcPacer.CollectIfReserved();

            Native.SleepUntil(plannedWake);
            wakeTime = Native.MonotonicNs();

#if RASTER_METRICS
            UpdateSync.NoteWake(wakeTime);
            wakeCollections = GC.CollectionCount(0);
            intervalCollectionsIdle += wakeCollections - planCollections;
            drawEnd = 0;

            samplingCpu = ++cpuSampleCounter % cpu_sample_interval == 0;
            wakeCpuTime = samplingCpu ? Native.ThreadCpuNs() : 0;

            long overshoot = Math.Max(0, wakeTime - plannedWake);

            wakeOvershoots.Add(overshoot);
            intervalMaxWake = Math.Max(intervalMaxWake, overshoot);
#endif
        }

        /// <summary>
        /// The number of slices to split refreshes into: as many as the setting allows that one present to the next still fits in.
        /// With more, frames would miss slices and present two or three slices apart, so tear lines would move between refreshes and frames would age unevenly.
        /// Draw thread.
        /// </summary>
        /// <remarks>
        /// A count that stops fitting is dropped at once, since every frame past that point would miss a slice. Moving back up waits for the
        /// higher count to fit with room to spare, and to keep fitting, so a frame time sitting near a slice boundary doesn't move the tear lines back and forth.
        /// </remarks>
        /// <param name="timing">The refresh being split.</param>
        /// <param name="frameNs">How long one present to the next takes.</param>
        private int chooseSliceCount(DrmVBlankClock.Timing timing, long frameNs)
        {
            int most = Math.Max(1, slices);
            frameNs = Math.Max(1, frameNs);

            int fits = (int)Math.Clamp(timing.PeriodNs / frameNs, 1, most);

#if RASTER_METRICS
            lastFits = fits;
#endif
            int fitsWithRoom = (int)Math.Clamp((long)(timing.PeriodNs * slice_raise_fit) / frameNs, 1, most);

            if (fits < sliceCount)
            {
                sliceCount = fits;
                moreSlicesFitSince = 0;
                moreSlicesFailedAt = 0;
            }
            else if (fitsWithRoom > sliceCount)
            {
                long now = Native.MonotonicNs();

                moreSlicesFailedAt = 0;

                if (moreSlicesFitSince == 0)
                    moreSlicesFitSince = now;
                else if (now - moreSlicesFitSince >= slice_raise_delay_ns)
                {
                    sliceCount = fitsWithRoom;
                    moreSlicesFitSince = 0;
                }
            }
            else if (moreSlicesFitSince != 0)
            {
                // The higher count has stopped fitting. Waiting a moment before throwing away the wait keeps a single
                // slow present from costing the seconds already served, which is what held the count below what it fit.
                long now = Native.MonotonicNs();

                if (moreSlicesFailedAt == 0)
                    moreSlicesFailedAt = now;
                else if (now - moreSlicesFailedAt >= slice_fit_grace_ns)
                {
                    moreSlicesFitSince = 0;
                    moreSlicesFailedAt = 0;
                }
            }

            return sliceCount;
        }

        /// <summary>
        /// Moves the render time prediction towards the one that leaves <see cref="late_target"/> of presents finishing late. Draw thread.
        /// </summary>
        /// <remarks>
        /// The prediction decides how long before its scanline a frame starts drawing, and a frame shows the scene as it was when it started.
        /// Predicting the slowest frames would hold every frame back to the speed of the slowest, so the prediction is pushed down until frames start finishing late.
        /// </remarks>
        private void adjustCostPercentile(double lateFraction)
        {
            if (lateFraction > late_target)
                costPercentile = Math.Min(cost_percentile_max, costPercentile + cost_percentile_step_up);
            else if (lateFraction < late_target / 2)
                costPercentile = Math.Max(cost_percentile_min, costPercentile - cost_percentile_step_down);
        }

#if RASTER_METRICS
        /// <summary>
        /// Logs where a frame's time went when the slice count changes, since that decides how many slices fit. Draw thread.
        /// </summary>
        private void logSliceCountChange(DrmVBlankClock.Timing timing, int from, int to, long frameNs, long headroom)
        {
            string rule = to > from ? $" More slices are only used once a frame fits in {slice_raise_fit:0%} of one, for {slice_raise_delay_ns / 1_000_000} ms." : string.Empty;

            Logger.Log($"Raster sync: {from} → {to} frame slices per refresh (up to {slices}). A slice at {to} lasts {ms(timing.PeriodNs / to)} ms, "
                       + $"and all but the slowest {1 - slice_fit_percentile:0.0%} of frames need {ms(frameNs)} ms: render {ms(renderCosts.Percentile(slice_fit_percentile))} ms "
                       + $"(draw {ms(draws.Percentile(slice_fit_percentile))} ms, GPU finish {ms(gpuFinishes.Percentile(slice_fit_percentile))} ms), headroom {ms(headroom)} ms, "
                       + $"and {ms(betweenPresents.Percentile(slice_fit_percentile))} ms from one swap to planning the next "
                       + $"(swap call {ms(swapCalls.Percentile(slice_fit_percentile))} ms, rest of the frame loop {ms(frameLoops.Percentile(slice_fit_percentile))} ms).{rule}");
        }
#endif

        /// <summary>
        /// Moves the tear line towards the offset found from recorded flips, which the play in progress keeps refitting. Draw thread.
        /// </summary>
        private double steerOffset(DrmVBlankClock.Timing timing)
        {
            if (finder?.GetOffset(timing) is not int target)
                return steeredOffset ?? 0;

            // Further off than that, as when a play starts, the tear line shows either way, so it goes straight there.
            double blankingLines = timing.VTotal - timing.VDisplay;

            if (steeredOffset is not double current || Math.Abs(target - current) > blankingLines / 2)
                steeredOffset = target;
            else
                steeredOffset = current + Math.Clamp(target - current, -max_steer_lines, max_steer_lines);

            return steeredOffset.Value;
        }

        /// <summary>
        /// Draw thread, once the frame has been submitted and before the GPU is waited on, so the draw thread's own work can be told from the GPU's.
        /// </summary>
        public void NoteDrawFinished()
        {
#if RASTER_METRICS
            drawEnd = Native.MonotonicNs();
            drawEndCollections = GC.CollectionCount(0);
            UpdateSync.NoteDrawn();

            if (samplingCpu)
                drawEndCpuTime = Native.ThreadCpuNs();
#endif
        }

        /// <summary>
        /// Holds a finished frame until its target scanline. Draw thread, after the GPU has finished the frame.
        /// </summary>
        public void WaitForPlannedPresent()
        {
            long ready = Native.MonotonicNs();
            long cost = ready - wakeTime;

            renderCosts.Add(cost);

#if RASTER_METRICS
            readyCollections = GC.CollectionCount(0);
            intervalMaxCost = Math.Max(intervalMaxCost, cost);

            if (drawEnd >= wakeTime)
            {
                long draw = drawEnd - wakeTime;
                long finish = ready - drawEnd;

                draws.Add(draw);
                gpuFinishes.Add(finish);

                intervalCollectionsDrawing += drawEndCollections - wakeCollections;
                intervalCollectionsGpu += readyCollections - drawEndCollections;

                if (samplingCpu)
                    drawCpuTimes.Add(Math.Max(0, drawEndCpuTime - wakeCpuTime));

                if (drawEndCollections != wakeCollections)
                {
                    drawsWithCollection.Add(draw);
                    intervalDrawsWithCollection++;
                    intervalMaxDrawWithCollection = Math.Max(intervalMaxDrawWithCollection, draw);
                }
                else
                    drawsWithoutCollection.Add(draw);

                intervalMaxDraw = Math.Max(intervalMaxDraw, draw);
                intervalMaxFinish = Math.Max(intervalMaxFinish, finish);
            }
#endif

            armProbe();

            long now = ready;
            var latch = host.PenLatch;

            if (latch?.HasDeferredDraw == true)
            {
                // The held cursor is drawn as close to the scanline as its own draw allows, so it takes the newest pen report there is.
                long lateStart = plannedTarget - lateDraws.Percentile(costPercentile);

                if (ready < lateStart)
                    Native.WaitUntil(lateStart, spin_window_ns);

                long drawStart = Native.MonotonicNs();

                latch!.DrawDeferred(host.Renderer);

#if RASTER_METRICS
                lateDrawSubmits.Add(Native.MonotonicNs() - drawStart);
#endif

                if (late_draw_waits_for_gpu)
                    host.FinishOnGpu();

                now = Native.MonotonicNs();
                lateDraws.Add(now - drawStart);

#if RASTER_METRICS
                intervalLateDraws++;
                intervalMaxLateDraw = Math.Max(intervalMaxLateDraw, now - drawStart);
#endif
            }

            if (now < plannedTarget)
            {
                Native.WaitUntil(plannedTarget, spin_window_ns);
                presentStart = Native.MonotonicNs();

#if RASTER_METRICS
                long error = Math.Max(0, presentStart - plannedTarget);

                timingErrors.Add(error);
                intervalMaxError = Math.Max(intervalMaxError, error);
#endif
            }
            else
            {
                // Present straight away. The tear line lands late this once, which beats holding the frame for another slice.
                presentStart = now;
                intervalLate++;
            }

#if RASTER_METRICS
            presentCollections = GC.CollectionCount(0);
            intervalCollectionsWaiting += presentCollections - readyCollections;

            UpdateSync.NotePresent(presentStart);
            host.PenLatch?.NotePresent(presentStart);
#endif
            probe?.NoteSwap(presentStart);

            if (probeArmed)
                probe!.MarkSwap(presentStart);
        }

        private void armProbe()
        {
            probeArmed = false;

            var timing = plannedTiming;

            if (!playing || finder == null || timing == null || ++presentsSinceProbe < probe_interval)
                return;

            if (probe == null || probe.Device != timing.Device || probe.CrtcId != timing.CrtcId)
            {
                probe?.Dispose();
                probe = new FlipProbe(timing.Device, timing.CrtcId, onFlip, onOvertaken, finder.AddMiss);
            }

            // Armed before the wait for the scanline, so the probe is already polling when the frame is swapped.
            probeArmed = probe.TryArm(timing);

            if (probeArmed)
                presentsSinceProbe = 0;
        }

        /// <summary>
        /// Probe thread.
        /// </summary>
        private void onFlip(DrmVBlankClock.Timing timing, long latencyNs)
        {
            Interlocked.Increment(ref intervalFlips);
            finder?.AddFlip(timing, latencyNs);
        }

        /// <summary>
        /// Probe thread.
        /// </summary>
        private void onOvertaken(DrmVBlankClock.Timing timing)
        {
            Interlocked.Increment(ref intervalOvertaken);
            finder?.AddOvertaken(timing);
        }

        /// <summary>
        /// Draw thread, once the buffers have been swapped.
        /// </summary>
        public void CompletePresent()
        {
            long end = Native.MonotonicNs();

            int endCollections = GC.CollectionCount(0);

            sampleCollectionPause(endCollections);

#if RASTER_METRICS
            intervalCollectionsSwapping += endCollections - presentCollections;
            swapCalls.Add(end - presentStart);
            intervalMaxSwap = Math.Max(intervalMaxSwap, end - presentStart);
            swapEnd = end;
#endif
            intervalPresents++;

            bool followsCursorOnly = lastTarget == lastCursorTarget && lastTarget != lastGridTarget;

#if RASTER_METRICS
            if (plannedTiming != null)
                pacing.NotePresent(plannedTiming, presentStart, plannedOffset, wakeTime);
#endif

            lastTarget = plannedTarget;

            if (plannedForCursor)
            {
#if RASTER_METRICS
                intervalCursorPresents++;

                if (plannedMerged)
                    intervalMergedPresents++;

                // A refresh whose slices all went to the cursor's present, as when the cursor is too close to a slice's tear line for both.
                if (followsCursorOnly && !plannedMerged)
                    intervalCursorOnlyRefreshes++;

                if (!double.IsNaN(lastCursorLine))
                {
                    double moved = Math.Abs(plannedCursorLine - lastCursorLine);

                    intervalCursorLineMoves += moved;
                    intervalMaxCursorLineMove = Math.Max(intervalMaxCursorLineMove, moved);
                }

                lastCursorLine = plannedCursorLine;
#endif
                lastCursorTarget = plannedTarget;
            }

            if (!plannedForCursor || plannedMerged)
                lastGridTarget = plannedTarget;
            planned = false;
            betweenPending = true;
            Interlocked.Increment(ref presentCount);

            if (end - intervalStart >= status_interval_ns)
            {
                int flips = Interlocked.Exchange(ref intervalFlips, 0);
                int overtaken = Interlocked.Exchange(ref intervalOvertaken, 0);
                double presentsPerSecond = intervalPresents * 1e9 / (end - intervalStart);
                double lateFraction = (double)intervalLate / intervalPresents;
                long margin = renderCosts.Percentile(costPercentile) + Interlocked.Read(ref headroomNs);

                string slicesText = mode == RasterSyncMode.FrameSlices ? $"{sliceCount} of up to {slices} slices per refresh, {intervalSkipped} slices skipped, " : string.Empty;
                string overtakenText = flips + overtaken > 0 ? $", {overtaken} of {flips + overtaken} timed frames overtaken by the next before they flipped" : string.Empty;

#if RASTER_METRICS
                // What a looser fit rule would have allowed, to size the next change without risking a skipped slice on this one.
                long looseFrameNs = renderCosts.Percentile(slice_fit_loose) + Interlocked.Read(ref headroomNs) + betweenPresents.Percentile(slice_fit_loose);
                long looseFits = plannedTiming == null ? 0 : Math.Clamp(plannedTiming.PeriodNs / Math.Max(1, looseFrameNs), 1, Math.Max(1, slices));

                int gen0 = GC.CollectionCount(0) - intervalGen0;
                int gen1 = GC.CollectionCount(1) - intervalGen1;
                int gen2 = GC.CollectionCount(2) - intervalGen2;

                status = $"{clock?.Status}. {presentsPerSecond:0} presents/s, {slicesText}"
                         + $"frames start {ms(margin)} ms before their scanline, "
                         + $"{intervalLate} late, {intervalMaxError / 1e3:0} µs present timing error, swap call up to {ms(intervalMaxSwap)} ms{overtakenText}";

                // The runtime log keeps what the status note shows, to line up with recordings of the screen afterwards.
                Logger.Log($"Raster sync: {presentsPerSecond:0} presents/s, {slicesText}{intervalLate} late ({lateFraction:0.0%}, aiming for {late_target:0%}), "
                           + $"{overtaken} of {flips + overtaken} timed frames overtaken, GC {gen0}/{gen1}/{gen2}. "
                           + $"Frames start {ms(margin)} ms before their scanline (render at the {costPercentile:0.0%} percentile, headroom {ms(Interlocked.Read(ref headroomNs))} ms). "
                           + $"All but the slowest {1 - slice_fit_percentile:0.0%} of frames need {ms(lastSlowFrameNs)} ms, which fits {lastFits} slices"
                           + $" (at {1 - slice_fit_loose:0%} it would be {ms(looseFrameNs)} ms and {looseFits} slices), "
                           + $"raising after {slice_raise_delay_ns / 1_000_000} ms with {slice_fit_grace_ns / 1_000_000} ms of grace.");

                Logger.Log($"Raster sync times, milliseconds at p50/p99/max: render {ms(renderCosts.Percentile(0.5))}/{ms(renderCosts.Percentile(fixed_percentile))}/{ms(intervalMaxCost)} "
                           + $"= draw {ms(draws.Percentile(0.5))}/{ms(draws.Percentile(fixed_percentile))}/{ms(intervalMaxDraw)} "
                           + $"+ GPU finish {ms(gpuFinishes.Percentile(0.5))}/{ms(gpuFinishes.Percentile(fixed_percentile))}/{ms(intervalMaxFinish)}. "
                           + $"Of that draw, processor time {ms(drawCpuTimes.Percentile(0.5))}/{ms(drawCpuTimes.Percentile(fixed_percentile))} sampled every {cpu_sample_interval} frames, "
                           + $"and {intervalDrawsWithCollection} draws had a collection run during them, worst {ms(intervalMaxDrawWithCollection)} this second "
                           + $"(p99 {ms(drawsWithCollection.Percentile(fixed_percentile))} against {ms(drawsWithoutCollection.Percentile(fixed_percentile))} without). "
                           + $"Wake overshoot {ms(wakeOvershoots.Percentile(0.5))}/{ms(wakeOvershoots.Percentile(fixed_percentile))}/{ms(intervalMaxWake)}, "
                           + $"swap call {ms(swapCalls.Percentile(0.5))}/{ms(swapCalls.Percentile(fixed_percentile))}/{ms(intervalMaxSwap)}, "
                           + $"rest of the frame loop {ms(frameLoops.Percentile(0.5))}/{ms(frameLoops.Percentile(fixed_percentile))}, "
                           + $"swap to next plan {ms(betweenPresents.Percentile(0.5))}/{ms(betweenPresents.Percentile(fixed_percentile))}/{ms(intervalMaxBetween)}, "
                           + $"present timing error {ms(timingErrors.Percentile(0.5))}/{ms(timingErrors.Percentile(fixed_percentile))}/{ms(intervalMaxError)}");

                int inPresent = intervalCollectionsDrawing + intervalCollectionsGpu + intervalCollectionsWaiting + intervalCollectionsSwapping;

                Logger.Log($"Raster sync GC: {gen0}/{gen1}/{gen2} collections, {intervalCollectionsIdle} of them while the draw thread slept and {inPresent} in its way "
                           + $"({intervalCollectionsDrawing} drawing, {intervalCollectionsGpu} waiting for the GPU, {intervalCollectionsWaiting} waiting for the scanline, "
                           + $"{intervalCollectionsSwapping} swapping). {intervalPauses} paused this second, totalling {ms(intervalPauseTotal)} ms, worst {ms(intervalPauseMax)} ms "
                           + $"(p50/p99 {ms(gcPauses.Percentile(0.5))}/{ms(gcPauses.Percentile(fixed_percentile))} ms over recent collections, which reach back further the rarer they are), "
                           + $"earned by {(intervalGcSamples > 0 ? intervalGcBytes / intervalGcSamples : lastGcBytes) / 1024} KiB allocated between collections. "
                           + $"{gcPacer.TakeForced()} were held for a gap before a frame, giving up {intervalCollectionSkips} slices to make one, "
                           + $"which costs a present {ms(gcPacer.ExpectedPauseNs)} ms against a {gcPacer.BudgetBytes / 1024} KiB budget.");

                Logger.Log(UpdateSync.TakeIntervalSummary(end - intervalStart));
                Logger.Log(pacing.TakeIntervalSummary());

                if (host.PenLatch != null)
                {
                    Logger.Log(host.PenLatch.TakeIntervalSummary()
                               + $" {intervalLateDraws} were drawn after the rest of the frame finished, taking {ms(lateDraws.Percentile(0.5))}/{ms(lateDraws.Percentile(fixed_percentile))}/{ms(intervalMaxLateDraw)} ms at p50/p99/max"
                               + $" (drawing and submitting {ms(lateDrawSubmits.Percentile(0.5))}/{ms(lateDrawSubmits.Percentile(fixed_percentile))}, {(late_draw_waits_for_gpu ? "then waiting for the GPU" : "not waiting for the GPU")})."
                               + $" {intervalCursorPresents} presents tore {cursor_tearline_lead} lines above the cursor, crowding out {intervalCursorSkips} slices,"
                               + $" and {intervalCursorOnlyRefreshes} followed another of theirs with no slice between, while {intervalMergedPresents} shared a slice's present."
                               + $" Their tear line moved {(intervalCursorPresents > 1 ? intervalCursorLineMoves / (intervalCursorPresents - 1) : 0):0} scanlines between them on average, {intervalMaxCursorLineMove:0} at most"
                               + $" ({(cursor_tearline_bands > 0 ? $"held to {cursor_tearline_bands} positions a refresh" : "following the cursor")}).");
                }
#else
                status = $"{clock?.Status}. {presentsPerSecond:0} presents/s, {slicesText}"
                         + $"frames start {ms(margin)} ms before their scanline, {intervalLate} late{overtakenText}";
#endif

                // Steered from a whole interval, so a single slow frame doesn't hold every later frame back.
                if (intervalPresents >= cost_adjust_min_presents)
                    adjustCostPercentile(lateFraction);

                startInterval(end);
            }
        }

        /// <summary>
        /// Counts a present that was not timed. Draw thread.
        /// </summary>
        public void CountPresent() => Interlocked.Increment(ref presentCount);

        /// <summary>
        /// Records what the runtime says the latest ephemeral collection cost, and how much was allocated to earn it. Draw thread.
        /// </summary>
        /// <remarks>
        /// Reading the memory info costs 57 ns and allocates 288 bytes, which is not something every present should pay,
        /// so it is only read once the collection count says one has actually happened: a dozen times a second rather than
        /// several hundred. Several presents can follow one collection, so the index the runtime gives it tells repeats apart.
        /// </remarks>
        private void sampleCollectionPause(int collections)
        {
            if (collections == lastCollections)
                return;

            lastCollections = collections;

            var info = GC.GetGCMemoryInfo(GCKind.Ephemeral);

            if (info.Index == gcIndex)
                return;

            gcIndex = info.Index;

            long pause = info.PauseDurations.Length > 0 ? (long)info.PauseDurations[0].TotalNanoseconds : 0;

#if RASTER_METRICS
            if (pause > 0)
            {
                gcPauses.Add(pause);
                intervalPauses++;
                intervalPauseTotal += pause;
                intervalPauseMax = Math.Max(intervalPauseMax, pause);
            }
#endif

            // What the collection had to be earned by, which is the budget the pacer has to get to first.
            long allocated = GC.GetTotalAllocatedBytes();

            if (gcAllocated > 0)
            {
                lastGcBytes = allocated - gcAllocated;
#if RASTER_METRICS
                intervalGcBytes += lastGcBytes;
                intervalGcSamples++;
#endif
            }

            gcAllocated = allocated;
            gcPacer.NoteCollection(allocated, lastGcBytes, pause);
        }

        private void startInterval(long now)
        {
            intervalStart = now;
            intervalPresents = 0;
            intervalLate = 0;
            intervalSkipped = 0;
#if RASTER_METRICS
            intervalCollectionSkips = 0;
            intervalDrawsWithCollection = 0;
            intervalMaxDrawWithCollection = 0;
            intervalCollectionsIdle = 0;
            intervalCollectionsDrawing = 0;
            intervalCollectionsGpu = 0;
            intervalCollectionsWaiting = 0;
            intervalCollectionsSwapping = 0;
            intervalGcBytes = 0;
            intervalGcSamples = 0;
            intervalPauses = 0;
            intervalPauseTotal = 0;
            intervalPauseMax = 0;
            intervalMaxCost = 0;
            intervalMaxDraw = 0;
            intervalMaxFinish = 0;
            intervalMaxWake = 0;
            intervalMaxSwap = 0;
            intervalMaxBetween = 0;
            intervalMaxError = 0;
            intervalGen0 = GC.CollectionCount(0);
            intervalGen1 = GC.CollectionCount(1);
            intervalGen2 = GC.CollectionCount(2);
            intervalLateDraws = 0;
            intervalCursorPresents = 0;
            intervalCursorSkips = 0;
            intervalCursorOnlyRefreshes = 0;
            intervalMergedPresents = 0;
            intervalCursorLineMoves = 0;
            intervalMaxCursorLineMove = 0;
            intervalMaxLateDraw = 0;
#endif
            Interlocked.Exchange(ref intervalFlips, 0);
            Interlocked.Exchange(ref intervalOvertaken, 0);
        }

        private static string ms(long ns) => $"{ns / 1e6:0.000}";

        private static long ceilingDivide(long dividend, long divisor) =>
            dividend >= 0 ? (dividend + divisor - 1) / divisor : -(-dividend / divisor);

        public void Dispose()
        {
            probe?.Dispose();
            probe = null;

            // Exiting mid-play still keeps what was recorded.
            finder?.EndPlay(waitForSave: true);

            clock?.Dispose();
            clock = null;
        }
    }
}

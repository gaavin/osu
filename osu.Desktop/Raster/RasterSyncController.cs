// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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
        /// The fraction of recent presents the render time prediction covers. Frames slower than that finish late and present straight away.
        /// </summary>
        private const double cost_percentile = 0.99;

        /// <summary>
        /// How long more frame slices have to keep fitting before refreshes are split into them, so tear lines don't hop between counts.
        /// </summary>
        private const long slice_raise_delay_ns = 2_000_000_000;

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
        private readonly DurationWindow renderCosts = new DurationWindow(cost_history);

        /// <summary>
        /// From starting one timed swap to planning the next present: <see cref="swapCalls"/>, then <see cref="frameLoops"/>.
        /// </summary>
        private readonly DurationWindow betweenPresents = new DurationWindow(cost_history);

        private readonly DurationWindow swapCalls = new DurationWindow(cost_history);

        /// <summary>
        /// From a timed swap returning to planning the next present.
        /// </summary>
        private readonly DurationWindow frameLoops = new DurationWindow(cost_history);

        /// <summary>
        /// How late presents that waited for their scanline started, which is how far their tear lines land past the one they were aimed at.
        /// </summary>
        private readonly DurationWindow timingErrors = new DurationWindow(cost_history);

        private bool betweenPending;
        private int sliceCount = 1;
        private long moreSlicesFitSince;
        private bool planned;
        private DrmVBlankClock.Timing? plannedTiming;
        private long plannedTarget;
        private int? plannedSlice;
        private long lastTarget;
        private double? steeredOffset;
        private long wakeTime;
        private long presentStart;
        private long swapEnd;
        private bool timerSlackSet;
        private string? blockedBy = "Off";

        private FlipProbe? probe;
        private bool probeArmed;
        private int presentsSinceProbe;

        private long intervalStart;
        private int intervalPresents;
        private int intervalLate;
        private int intervalSkipped;
        private long intervalMaxCost;
        private long intervalMaxSwap;
        private long intervalMaxBetween;
        private long intervalMaxError;

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
                return;
            }

            bool followsPresent = betweenPending;

            if (betweenPending)
            {
                long planStart = Native.MonotonicNs();

                betweenPresents.Add(planStart - presentStart);
                frameLoops.Add(planStart - swapEnd);
                intervalMaxBetween = Math.Max(intervalMaxBetween, planStart - presentStart);
                betweenPending = false;
            }

            long renderNs = renderCosts.Percentile(cost_percentile);
            long headroom = Interlocked.Read(ref headroomNs);
            long cost = renderNs + headroom;

            int previousCount = sliceCount;
            int count = 1;

            if (mode == RasterSyncMode.FrameSlices)
            {
                count = chooseSliceCount(timing, cost + betweenPresents.Percentile(cost_percentile));

                if (count != previousCount)
                    logSliceCountChange(timing, previousCount, count, renderNs, headroom);
            }

            long slice = timing.PeriodNs / count;

            // The first tear line of each refresh aims at the middle of the blanking interval, moved by the offset.
            // Further slices follow at even spacing down the screen.
            double tearline = (timing.VDisplay + timing.VTotal) / 2.0 + steerOffset(timing);
            long anchor = timing.VBlankNs + (long)(tearline * timing.PeriodNs / timing.VTotal);

            long now = Native.MonotonicNs();
            long target = anchor + ceilingDivide(now + cost - anchor, slice) * slice;

            // A frame that finished early would otherwise tear into the slice the previous frame went to.
            if (target - lastTarget < slice / 2)
                target += slice;

            // Slices left without a frame of their own since the last present.
            if (followsPresent && count == previousCount && target - lastTarget > slice * 3 / 2)
                intervalSkipped += (int)((target - lastTarget + slice / 2) / slice) - 1;

            // Targets are whole slices from the anchor, whose slice is the one at the top of the screen.
            long sliceNumber = (target - anchor) / slice;
            plannedSlice = count > 1 ? (int)((sliceNumber % count + count) % count) : null;

            plannedTiming = timing;
            plannedTarget = target;
            planned = true;

            Native.SleepUntil(target - cost);
            wakeTime = Native.MonotonicNs();
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
            int fitsWithRoom = (int)Math.Clamp((long)(timing.PeriodNs * slice_raise_fit) / frameNs, 1, most);

            if (fits < sliceCount)
            {
                sliceCount = fits;
                moreSlicesFitSince = 0;
            }
            else if (fitsWithRoom > sliceCount)
            {
                if (moreSlicesFitSince == 0)
                    moreSlicesFitSince = Native.MonotonicNs();
                else if (Native.MonotonicNs() - moreSlicesFitSince >= slice_raise_delay_ns)
                {
                    sliceCount = fitsWithRoom;
                    moreSlicesFitSince = 0;
                }
            }
            else
                moreSlicesFitSince = 0;

            return sliceCount;
        }

        /// <summary>
        /// Logs where a frame's time went when the slice count changes, since that decides how many slices fit. Draw thread.
        /// </summary>
        private void logSliceCountChange(DrmVBlankClock.Timing timing, int from, int to, long renderNs, long headroom)
        {
            long between = betweenPresents.Percentile(cost_percentile);
            string rule = to > from ? $" More slices are only used once a frame fits in {slice_raise_fit:0%} of one, for {slice_raise_delay_ns / 1_000_000_000} seconds." : string.Empty;

            Logger.Log($"Raster sync: {from} → {to} frame slices per refresh (up to {slices}). A slice at {to} lasts {ms(timing.PeriodNs / to)} ms, "
                       + $"and a frame needs {ms(renderNs + headroom + between)} ms: render {ms(renderNs)} ms, headroom {ms(headroom)} ms, "
                       + $"and {ms(between)} ms from one swap to planning the next (swap call {ms(swapCalls.Percentile(cost_percentile))} ms, "
                       + $"rest of the frame loop {ms(frameLoops.Percentile(cost_percentile))} ms). Durations are 99th percentiles of the last {cost_history} presents.{rule}");
        }

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
        /// Holds a finished frame until its target scanline. Draw thread, after the GPU has finished the frame.
        /// </summary>
        public void WaitForPlannedPresent()
        {
            long ready = Native.MonotonicNs();
            long cost = ready - wakeTime;

            renderCosts.Add(cost);
            intervalMaxCost = Math.Max(intervalMaxCost, cost);

            armProbe();

            if (ready < plannedTarget)
            {
                Native.WaitUntil(plannedTarget, spin_window_ns);
                presentStart = Native.MonotonicNs();

                long error = Math.Max(0, presentStart - plannedTarget);

                timingErrors.Add(error);
                intervalMaxError = Math.Max(intervalMaxError, error);
            }
            else
            {
                // Present straight away. The tear line lands late this once, which beats holding the frame for another slice.
                presentStart = ready;
                intervalLate++;
            }

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

            swapCalls.Add(end - presentStart);
            intervalMaxSwap = Math.Max(intervalMaxSwap, end - presentStart);
            intervalPresents++;

            swapEnd = end;
            lastTarget = plannedTarget;
            planned = false;
            betweenPending = true;
            Interlocked.Increment(ref presentCount);

            if (end - intervalStart >= status_interval_ns)
            {
                int flips = Interlocked.Exchange(ref intervalFlips, 0);
                int overtaken = Interlocked.Exchange(ref intervalOvertaken, 0);
                double presentsPerSecond = intervalPresents * 1e9 / (end - intervalStart);

                string slicesText = mode == RasterSyncMode.FrameSlices ? $"{sliceCount} of up to {slices} slices per refresh, {intervalSkipped} slices skipped, " : string.Empty;
                string overtakenText = flips + overtaken > 0 ? $", {overtaken} of {flips + overtaken} timed frames overtaken by the next before they flipped" : string.Empty;

                status = $"{clock?.Status}. {presentsPerSecond:0} presents/s, {slicesText}"
                         + $"render {ms(renderCosts.Percentile(cost_percentile))} ms at the 99th percentile and up to {ms(intervalMaxCost)} ms, "
                         + $"{intervalLate} late, {intervalMaxError / 1e3:0} µs present timing error, swap call up to {ms(intervalMaxSwap)} ms{overtakenText}";

                // The runtime log keeps what the status note shows, to line up with recordings of the screen afterwards.
                Logger.Log($"Raster sync: {presentsPerSecond:0} presents/s, {slicesText}{intervalLate} late, "
                           + $"{overtaken} of {flips + overtaken} timed frames overtaken. Milliseconds at p50/p99/max: "
                           + $"render {ms(renderCosts.Percentile(0.5))}/{ms(renderCosts.Percentile(cost_percentile))}/{ms(intervalMaxCost)}, "
                           + $"swap call {ms(swapCalls.Percentile(0.5))}/{ms(swapCalls.Percentile(cost_percentile))}/{ms(intervalMaxSwap)}, "
                           + $"rest of the frame loop {ms(frameLoops.Percentile(0.5))}/{ms(frameLoops.Percentile(cost_percentile))}, "
                           + $"swap to next plan {ms(betweenPresents.Percentile(0.5))}/{ms(betweenPresents.Percentile(cost_percentile))}/{ms(intervalMaxBetween)}, "
                           + $"present timing error {ms(timingErrors.Percentile(0.5))}/{ms(timingErrors.Percentile(cost_percentile))}/{ms(intervalMaxError)}, "
                           + $"headroom {ms(Interlocked.Read(ref headroomNs))}");

                startInterval(end);
            }
        }

        /// <summary>
        /// Counts a present that was not timed. Draw thread.
        /// </summary>
        public void CountPresent() => Interlocked.Increment(ref presentCount);

        private void startInterval(long now)
        {
            intervalStart = now;
            intervalPresents = 0;
            intervalLate = 0;
            intervalSkipped = 0;
            intervalMaxCost = 0;
            intervalMaxSwap = 0;
            intervalMaxBetween = 0;
            intervalMaxError = 0;
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

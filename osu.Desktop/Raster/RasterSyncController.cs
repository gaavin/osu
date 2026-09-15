// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using osu.Framework.Bindables;
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
        private const int cost_history = 64;

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
        private readonly long[] costs = new long[cost_history];
        private int nextCost;
        private bool planned;
        private DrmVBlankClock.Timing? plannedTiming;
        private long plannedTarget;
        private long lastTarget;
        private double? steeredOffset;
        private long wakeTime;
        private long presentStart;
        private bool timerSlackSet;
        private string? blockedBy = "Off";

        private FlipProbe? probe;
        private bool probeArmed;
        private int presentsSinceProbe;

        private long intervalStart;
        private int intervalPresents;
        private int intervalLate;
        private long intervalMaxCost;
        private long intervalMaxSwap;
        private long intervalMaxError;

        public long PresentCount => Interlocked.Read(ref presentCount);

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

            int count = mode == RasterSyncMode.FrameSlices ? Math.Max(1, slices) : 1;
            long slice = timing.PeriodNs / count;
            long cost = costs.Max() + Interlocked.Read(ref headroomNs);

            // The first tear line of each refresh aims at the middle of the blanking interval, moved by the offset.
            // Further slices follow at even spacing down the screen.
            double tearline = (timing.VDisplay + timing.VTotal) / 2.0 + steerOffset(timing);
            long anchor = timing.VBlankNs + (long)(tearline * timing.PeriodNs / timing.VTotal);

            long now = Native.MonotonicNs();
            long target = anchor + ceilingDivide(now + cost - anchor, slice) * slice;

            // A frame that finished early would otherwise tear into the slice the previous frame went to.
            if (target - lastTarget < slice / 2)
                target += slice;

            plannedTiming = timing;
            plannedTarget = target;
            planned = true;

            Native.SleepUntil(target - cost);
            wakeTime = Native.MonotonicNs();
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

            costs[nextCost] = cost;
            nextCost = (nextCost + 1) % cost_history;
            intervalMaxCost = Math.Max(intervalMaxCost, cost);

            armProbe();

            if (ready < plannedTarget)
            {
                Native.WaitUntil(plannedTarget, spin_window_ns);
                presentStart = Native.MonotonicNs();
                intervalMaxError = Math.Max(intervalMaxError, presentStart - plannedTarget);
            }
            else
            {
                // Present straight away. The tear line lands late this once, which beats holding the frame for another slice.
                presentStart = ready;
                intervalLate++;
            }

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
                probe = new FlipProbe(timing.Device, timing.CrtcId, finder.AddFlip, finder.AddMiss);
            }

            // Armed before the wait for the scanline, so the probe is already polling when the frame is swapped.
            probeArmed = probe.TryArm(timing);

            if (probeArmed)
                presentsSinceProbe = 0;
        }

        /// <summary>
        /// Draw thread, once the buffers have been swapped.
        /// </summary>
        public void CompletePresent()
        {
            long end = Native.MonotonicNs();

            intervalMaxSwap = Math.Max(intervalMaxSwap, end - presentStart);
            intervalPresents++;

            lastTarget = plannedTarget;
            planned = false;
            Interlocked.Increment(ref presentCount);

            if (end - intervalStart >= status_interval_ns)
            {
                status = $"{clock?.Status}. {intervalPresents * 1e9 / (end - intervalStart):0} presents/s, render up to {intervalMaxCost / 1e6:0.00} ms, "
                         + $"{intervalLate} late, {intervalMaxError / 1e3:0} µs present timing error, swap call up to {intervalMaxSwap / 1e6:0.00} ms";

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
            intervalMaxCost = 0;
            intervalMaxSwap = 0;
            intervalMaxError = 0;
        }

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

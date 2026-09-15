// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using osu.Framework.Logging;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Follows the vertical blanking of the CRTC showing the game, using the kernel's own vblank timestamps.
    /// </summary>
    /// <remarks>
    /// The compositor holds DRM master, but reading CRTC modes and waiting on vblank are open to any client of the primary node.
    /// The kernel stamps a vblank at the end of the blanking interval, which is when line 0 of the next frame starts scanning out.
    /// </remarks>
    internal sealed unsafe class DrmVBlankClock : IDisposable
    {
        /// <summary>
        /// The mode SDL reports for the window's display, used to pick a CRTC when there are several.
        /// </summary>
        public sealed record DisplayHint(int Width, int Height, float RefreshRate);

        /// <summary>
        /// Blanking of one CRTC. Line <c>n</c> starts scanning out <c>n * PeriodNs / VTotal</c> after <c>VBlankNs</c>,
        /// the CLOCK_MONOTONIC time line 0 of the latest frame started scanning out, smoothed over recent vblanks.
        /// </summary>
        public sealed record Timing(int VDisplay, int VTotal, long PeriodNs, long VBlankNs);

        private const int fit_window = 256;

        /// <summary>
        /// Vblanks to average before the fitted period replaces the one the mode implies.
        /// </summary>
        private const int min_fit_samples = 16;

        private readonly Func<DisplayHint?> getHint;

        private volatile bool disposed;
        private volatile Timing? timing;
        private volatile string status = "Looking for the display";

        /// <summary>
        /// The latest blanking, or null while no CRTC is being followed.
        /// </summary>
        public Timing? Current => timing;

        /// <summary>
        /// Which CRTC is followed, or why none is.
        /// </summary>
        public string Status => status;

        public DrmVBlankClock(Func<DisplayHint?> getHint)
        {
            this.getHint = getHint;

            new Thread(run)
            {
                IsBackground = true,
                Name = "VBlank (raster sync)",
            }.Start();
        }

        private void run()
        {
            Native.SetTimerSlack(1);

            while (!disposed)
            {
                using (var crtc = selectCrtc())
                {
                    if (crtc != null)
                        follow(crtc);
                }

                timing = null;

                if (!disposed)
                    Thread.Sleep(1000);
            }
        }

        private sealed class Crtc : IDisposable
        {
            public required string Device { get; init; }
            public required int Fd { get; init; }
            public required int Index { get; init; }
            public required uint Id { get; init; }
            public required Native.DrmModeModeInfo Mode { get; init; }

            public double RefreshRate => Mode.Clock * 1000.0 / (Mode.HTotal * Mode.VTotal);

            public bool Matches(DisplayHint hint) =>
                Mode.HDisplay == hint.Width && Mode.VDisplay == hint.Height && Math.Abs(RefreshRate - hint.RefreshRate) < 1;

            public void Dispose() => Native.close(Fd);
        }

        private Crtc? selectCrtc()
        {
            string? devicePath = Environment.GetEnvironmentVariable("OSU_RASTER_DRM_DEVICE");
            string? crtcIdString = Environment.GetEnvironmentVariable("OSU_RASTER_CRTC");
            uint? crtcId = uint.TryParse(crtcIdString, out uint id) ? id : null;

            string[] devices;

            try
            {
                devices = devicePath != null
                    ? new[] { devicePath }
                    : Directory.GetFiles("/dev/dri", "card*").Order(StringComparer.Ordinal).ToArray();
            }
            catch (Exception e)
            {
                status = $"No DRM devices: {e.Message}";
                return null;
            }

            var candidates = new List<Crtc>();
            string? error = null;

            foreach (string device in devices)
            {
                int fd = Native.open(device, Native.O_RDWR | Native.O_CLOEXEC);

                if (fd < 0)
                {
                    error = $"Cannot open {device}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}";
                    continue;
                }

                int found = 0;

                foreach (var (index, crtcMode, idOfCrtc) in activeCrtcs(fd))
                {
                    if (crtcId != null && idOfCrtc != crtcId)
                        continue;

                    candidates.Add(new Crtc { Device = device, Fd = found++ == 0 ? fd : duplicate(fd), Index = index, Id = idOfCrtc, Mode = crtcMode });
                }

                if (found == 0)
                    Native.close(fd);
            }

            if (candidates.Count == 0)
            {
                status = error ?? (crtcId != null ? $"CRTC {crtcId} is not lit" : "No lit CRTC found");
                return null;
            }

            DisplayHint? hint = getHint();

            Crtc chosen = (hint != null ? candidates.FirstOrDefault(c => c.Matches(hint)) : null) ?? candidates[0];

            foreach (var c in candidates.Where(c => c != chosen))
                c.Dispose();

            if (candidates.Count > 1 && (hint == null || !chosen.Matches(hint)))
                Logger.Log($"Raster sync could not tell which of {candidates.Count} displays shows the game; set OSU_RASTER_DRM_DEVICE and OSU_RASTER_CRTC to choose.", LoggingTarget.Runtime, LogLevel.Important);

            return chosen;
        }

        private static int duplicate(int fd) => Native.open($"/proc/self/fd/{fd}", Native.O_RDWR | Native.O_CLOEXEC);

        private static IEnumerable<(int index, Native.DrmModeModeInfo mode, uint id)> activeCrtcs(int fd)
        {
            var result = new List<(int, Native.DrmModeModeInfo, uint)>();

            Native.DrmModeCardRes res = default;
            if (Native.ioctl(fd, Native.DRM_IOCTL_MODE_GETRESOURCES, &res) != 0 || res.CountCrtcs == 0)
                return result;

            uint[] ids = new uint[res.CountCrtcs];

            fixed (uint* idsPtr = ids)
            {
                res = new Native.DrmModeCardRes
                {
                    CrtcIdPtr = (ulong)idsPtr,
                    CountCrtcs = (uint)ids.Length,
                };

                if (Native.ioctl(fd, Native.DRM_IOCTL_MODE_GETRESOURCES, &res) != 0)
                    return result;
            }

            // The kernel numbers CRTCs in the order it lists them, and vblank requests address them by that number.
            for (int i = 0; i < Math.Min(ids.Length, (int)res.CountCrtcs); i++)
            {
                if (!tryGetMode(fd, ids[i], out var mode))
                    continue;

                if ((mode.Flags & (Native.DRM_MODE_FLAG_INTERLACE | Native.DRM_MODE_FLAG_DBLSCAN)) != 0)
                    continue;

                result.Add((i, mode, ids[i]));
            }

            return result;
        }

        private static bool tryGetMode(int fd, uint crtcId, out Native.DrmModeModeInfo mode)
        {
            Native.DrmModeCrtc crtc = new Native.DrmModeCrtc { CrtcId = crtcId };
            mode = default;

            if (Native.ioctl(fd, Native.DRM_IOCTL_MODE_GETCRTC, &crtc) != 0 || crtc.ModeValid == 0 || crtc.Mode.Clock == 0 || crtc.Mode.HTotal == 0 || crtc.Mode.VTotal == 0)
                return false;

            mode = crtc.Mode;
            return true;
        }

        private static bool sameTiming(Native.DrmModeModeInfo a, Native.DrmModeModeInfo b) =>
            a.Clock == b.Clock && a.HDisplay == b.HDisplay && a.HTotal == b.HTotal && a.VDisplay == b.VDisplay && a.VTotal == b.VTotal;

        private void follow(Crtc crtc)
        {
            var mode = crtc.Mode;
            long modePeriodNs = (long)mode.HTotal * mode.VTotal * 1_000_000 / mode.Clock;
            string description = $"{crtc.Device} CRTC {crtc.Id}, {mode.HDisplay}x{mode.VDisplay} at {crtc.RefreshRate:0.000} Hz, {mode.VTotal - mode.VDisplay} blanking lines";

            Logger.Log($"Raster sync following {description}");

            uint type = Native.DRM_VBLANK_RELATIVE | (((uint)crtc.Index << Native.DRM_VBLANK_HIGH_CRTC_SHIFT) & Native.DRM_VBLANK_HIGH_CRTC_MASK);

            // Sequence numbers and timestamps of recent vblanks, as a ring.
            long[] sequences = new long[fit_window];
            long[] times = new long[fit_window];
            int count = 0;
            int next = 0;

            long sequence = 0;
            uint lastRawSequence = 0;
            long lastModeCheck = 0;
            string? lastRejection = null;
            int irregularVBlanks = 0;

            status = description;

            while (!disposed)
            {
                var vblank = new Native.DrmWaitVBlank { Type = type, Sequence = 1 };

                if (Native.ioctl(crtc.Fd, Native.DRM_IOCTL_WAIT_VBLANK, &vblank) != 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == Native.EINTR)
                        continue;

                    // Typically the display was turned off.
                    status = $"Waiting for vblank on {crtc.Device} failed: {Marshal.GetPInvokeErrorMessage(errno)}";
                    return;
                }

                long time = vblank.TvSec * 1_000_000_000 + vblank.TvUsec * 1000;

                uint advanced = unchecked(vblank.Sequence - lastRawSequence);
                lastRawSequence = vblank.Sequence;

                // A long gap means the CRTC was off or vblank interrupts were disabled, and the old samples no longer line up.
                if (count > 0 && (advanced == 0 || advanced > 1000))
                    count = next = 0;

                sequence = count == 0 ? 0 : sequence + advanced;

                // So does a vblank far from where the others put it.
                if (count > 0 && Math.Abs(time - predict(sequences, times, count, next, modePeriodNs, sequence)) > 1_000_000)
                {
                    count = next = 0;
                    sequence = 0;
                    irregularVBlanks++;
                }

                sequences[next] = sequence;
                times[next] = time;
                next = (next + 1) % fit_window;
                count = Math.Min(count + 1, fit_window);

                if (count < min_fit_samples)
                {
                    timing = null;

                    // With VRR the blanking stretches to follow presents, and there is no scanout to race.
                    if (irregularVBlanks >= 8)
                    {
                        string rejection = $"Vblanks on {crtc.Device} CRTC {crtc.Id} arrive irregularly. Is variable refresh rate on?";

                        if (rejection != lastRejection)
                            Logger.Log(rejection, LoggingTarget.Runtime, LogLevel.Important);

                        status = lastRejection = rejection;
                    }
                }
                else
                {
                    irregularVBlanks = 0;

                    fit(sequences, times, count, out double periodNs, out double offsetNs);

                    // With VRR the blanking stretches to follow presents, and there is no scanout to race.
                    if (Math.Abs(periodNs - modePeriodNs) > modePeriodNs * 0.01)
                    {
                        string rejection = $"Vblanks arrive every {periodNs / 1e6:0.000} ms but {crtc.Device} CRTC {crtc.Id} is set to {modePeriodNs / 1e6:0.000} ms. Is variable refresh rate on?";

                        if (rejection != lastRejection)
                            Logger.Log(rejection, LoggingTarget.Runtime, LogLevel.Important);

                        status = lastRejection = rejection;
                        timing = null;
                    }
                    else
                    {
                        if (lastRejection != null)
                        {
                            status = description;
                            lastRejection = null;
                        }

                        timing = new Timing(mode.VDisplay, mode.VTotal, (long)Math.Round(periodNs), (long)Math.Round(offsetNs + periodNs * sequence));
                    }
                }

                // Mode sets happen outside the game's control, and so does moving the window to another display.
                if (time - lastModeCheck > 1_000_000_000)
                {
                    lastModeCheck = time;

                    if (!tryGetMode(crtc.Fd, crtc.Id, out var current) || !sameTiming(current, mode))
                    {
                        Logger.Log($"Raster sync: the mode on {crtc.Device} CRTC {crtc.Id} changed");
                        return;
                    }

                    DisplayHint? hint = getHint();
                    if (hint != null && !crtc.Matches(hint) && hasOtherMatch(crtc, hint))
                        return;
                }
            }
        }

        private static bool hasOtherMatch(Crtc crtc, DisplayHint hint)
        {
            foreach (string device in Directory.GetFiles("/dev/dri", "card*"))
            {
                int fd = Native.open(device, Native.O_RDWR | Native.O_CLOEXEC);
                if (fd < 0)
                    continue;

                try
                {
                    foreach (var (_, mode, id) in activeCrtcs(fd))
                    {
                        bool isFollowed = device == crtc.Device && id == crtc.Id;

                        if (!isFollowed && mode.HDisplay == hint.Width && mode.VDisplay == hint.Height
                            && Math.Abs(mode.Clock * 1000.0 / (mode.HTotal * mode.VTotal) - hint.RefreshRate) < 1)
                            return true;
                    }
                }
                finally
                {
                    Native.close(fd);
                }
            }

            return false;
        }

        /// <summary>
        /// Least-squares line through (sequence, time), so single timestamps' microsecond rounding and interrupt jitter average out.
        /// Time for a sequence is <c>offsetNs + periodNs * sequence</c>.
        /// </summary>
        private static void fit(long[] sequences, long[] times, int count, out double periodNs, out double offsetNs)
        {
            long x0 = sequences[0];
            long y0 = times[0];

            double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;

            for (int i = 0; i < count; i++)
            {
                double x = sequences[i] - x0;
                double y = times[i] - y0;

                sumX += x;
                sumY += y;
                sumXX += x * x;
                sumXY += x * y;
            }

            double denominator = count * sumXX - sumX * sumX;

            periodNs = (count * sumXY - sumX * sumY) / denominator;
            offsetNs = y0 + (sumY - periodNs * sumX) / count - periodNs * x0;
        }

        private static long predict(long[] sequences, long[] times, int count, int next, long modePeriodNs, long sequence)
        {
            int last = (next + fit_window - 1) % fit_window;

            if (count < min_fit_samples)
                return times[last] + (sequence - sequences[last]) * modePeriodNs;

            fit(sequences, times, count, out double periodNs, out double offsetNs);
            return (long)(offsetNs + periodNs * sequence);
        }

        public void Dispose()
        {
            // The thread notices within one refresh, and closes the device itself.
            disposed = true;
            timing = null;
        }
    }
}

// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Runtime.InteropServices;
using System.Threading;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// The libc and DRM calls raster sync needs. Linux x86_64 only.
    /// </summary>
    internal static unsafe class Native
    {
        public const int O_RDWR = 2;
        public const int O_CLOEXEC = 0x80000;
        public const int EINTR = 4;

        public const ulong DRM_IOCTL_WAIT_VBLANK = 0xC018643A;
        public const ulong DRM_IOCTL_MODE_GETRESOURCES = 0xC04064A0;
        public const ulong DRM_IOCTL_MODE_GETCRTC = 0xC06864A1;

        public const uint DRM_VBLANK_RELATIVE = 0x1;
        public const int DRM_VBLANK_HIGH_CRTC_SHIFT = 1;
        public const uint DRM_VBLANK_HIGH_CRTC_MASK = 0x3e;

        public const uint DRM_MODE_FLAG_INTERLACE = 1 << 4;
        public const uint DRM_MODE_FLAG_DBLSCAN = 1 << 5;

        private const int clock_monotonic = 1;
        private const int timer_abstime = 1;
        private const int pr_set_timerslack = 29;

        [StructLayout(LayoutKind.Sequential)]
        private struct TimeSpec
        {
            public long Seconds;
            public long NanoSeconds;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DrmModeCardRes
        {
            public ulong FbIdPtr;
            public ulong CrtcIdPtr;
            public ulong ConnectorIdPtr;
            public ulong EncoderIdPtr;
            public uint CountFbs;
            public uint CountCrtcs;
            public uint CountConnectors;
            public uint CountEncoders;
            public uint MinWidth;
            public uint MaxWidth;
            public uint MinHeight;
            public uint MaxHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DrmModeModeInfo
        {
            public uint Clock;
            public ushort HDisplay;
            public ushort HSyncStart;
            public ushort HSyncEnd;
            public ushort HTotal;
            public ushort HSkew;
            public ushort VDisplay;
            public ushort VSyncStart;
            public ushort VSyncEnd;
            public ushort VTotal;
            public ushort VScan;
            public uint VRefresh;
            public uint Flags;
            public uint Type;
            public fixed byte Name[32];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DrmModeCrtc
        {
            public ulong SetConnectorsPtr;
            public uint CountConnectors;
            public uint CrtcId;
            public uint FbId;
            public uint X;
            public uint Y;
            public uint GammaSize;
            public uint ModeValid;
            public DrmModeModeInfo Mode;
        }

        /// <summary>
        /// union drm_wait_vblank: the request fills <see cref="Type"/>, <see cref="Sequence"/> and <see cref="Signal"/>,
        /// and the reply overwrites them with the sequence and CLOCK_MONOTONIC time of the vblank waited for.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct DrmWaitVBlank
        {
            [FieldOffset(0)]
            public uint Type;

            [FieldOffset(4)]
            public uint Sequence;

            [FieldOffset(8)]
            public ulong Signal;

            [FieldOffset(8)]
            public long TvSec;

            [FieldOffset(16)]
            public long TvUsec;
        }

        [DllImport("libc", SetLastError = true)]
        public static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        public static extern int ioctl(int fd, ulong request, void* arg);

        [DllImport("libc")]
        [SuppressGCTransition]
        private static extern int clock_gettime(int clockId, TimeSpec* tp);

        [DllImport("libc")]
        private static extern int clock_nanosleep(int clockId, int flags, TimeSpec* request, TimeSpec* remain);

        [DllImport("libc")]
        private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

        /// <summary>
        /// CLOCK_MONOTONIC in nanoseconds, the clock DRM stamps vblanks with.
        /// </summary>
        public static long MonotonicNs()
        {
            TimeSpec ts;
            clock_gettime(clock_monotonic, &ts);
            return ts.Seconds * 1_000_000_000 + ts.NanoSeconds;
        }

        /// <summary>
        /// Sleeps until an absolute <see cref="MonotonicNs"/> time. Returns straight away if it has passed.
        /// </summary>
        public static void SleepUntil(long ns)
        {
            TimeSpec ts = new TimeSpec
            {
                Seconds = ns / 1_000_000_000,
                NanoSeconds = ns % 1_000_000_000,
            };

            // clock_nanosleep returns the error rather than setting errno.
            while (clock_nanosleep(clock_monotonic, timer_abstime, &ts, null) == EINTR)
            {
            }
        }

        /// <summary>
        /// Sleeps until <paramref name="spinNs"/> before an absolute <see cref="MonotonicNs"/> time, then spins the rest of the way,
        /// since a sleep can overshoot by tens of microseconds.
        /// </summary>
        public static void WaitUntil(long ns, long spinNs)
        {
            if (ns - MonotonicNs() > spinNs)
                SleepUntil(ns - spinNs);

            while (MonotonicNs() < ns)
                Thread.SpinWait(10);
        }

        /// <summary>
        /// Lets the kernel wake the calling thread within 1 ns of its deadline, instead of the default 50 µs.
        /// </summary>
        public static void SetTimerSlack(ulong ns) => prctl(pr_set_timerslack, ns, 0, 0, 0);
    }
}

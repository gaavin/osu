// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Times how long the compositor takes to put a swapped frame on the display, by watching the framebuffer on the CRTC's primary plane change.
    /// </summary>
    /// <remarks>
    /// The kernel swaps in the new plane state as soon as it accepts the compositor's commit, so the change marks the flip to within the
    /// driver's own programming delay. Reading the CRTC holds its modeset locks for a couple of microseconds, so the probe only polls from
    /// just before a swap until the flip that follows it.
    /// Framebuffers don't say which swap they came from, so a flip that comes after the next frame was swapped can't be told apart from
    /// that next frame replacing this one. Such frames are counted as overtaken rather than timed.
    /// </remarks>
    internal sealed unsafe class FlipProbe : IDisposable
    {
        /// <summary>
        /// How long after a swap to give up on seeing its flip.
        /// </summary>
        private const long timeout_ns = 20_000_000;

        /// <summary>
        /// How long to wait for a swap to be marked at all.
        /// </summary>
        private const long swap_timeout_ns = 200_000_000;

        public readonly string Device;
        public readonly uint CrtcId;

        private readonly int fd;
        private readonly Action<DrmVBlankClock.Timing, long> onFlip;
        private readonly Action<DrmVBlankClock.Timing> onOvertaken;
        private readonly Action<DrmVBlankClock.Timing> onMiss;
        private readonly SemaphoreSlim wake = new SemaphoreSlim(0);

        private volatile bool disposed;
        private int busy;

        // Handed from the draw thread to the probe thread through wake.
        private uint baselineFb;
        private DrmVBlankClock.Timing timing = null!;
        private long swapTime;

        // Written by the draw thread for every timed swap, watched or not.
        private long latestSwapTime;

        /// <param name="device">The DRM primary node the CRTC belongs to.</param>
        /// <param name="crtcId">The CRTC showing the game.</param>
        /// <param name="onFlip">Called on the probe thread with the timing the frame was presented against and the time from swap to flip, in nanoseconds.</param>
        /// <param name="onOvertaken">Called on the probe thread when the next frame was swapped before any flip followed a swap.</param>
        /// <param name="onMiss">Called on the probe thread when no flip followed a swap.</param>
        public FlipProbe(string device, uint crtcId, Action<DrmVBlankClock.Timing, long> onFlip, Action<DrmVBlankClock.Timing> onOvertaken,
                         Action<DrmVBlankClock.Timing> onMiss)
        {
            Device = device;
            CrtcId = crtcId;
            this.onFlip = onFlip;
            this.onOvertaken = onOvertaken;
            this.onMiss = onMiss;

            fd = Native.open(device, Native.O_RDWR | Native.O_CLOEXEC);

            if (fd >= 0)
            {
                new Thread(run)
                {
                    IsBackground = true,
                    Name = "Flip probe (raster sync)",
                }.Start();
            }
        }

        /// <summary>
        /// Starts watching for the flip of a frame about to be swapped. Draw thread, before waiting for the frame's scanline.
        /// </summary>
        /// <returns>False if the previous flip is still being watched for.</returns>
        public bool TryArm(DrmVBlankClock.Timing timing)
        {
            if (fd < 0 || disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                return false;

            if (!tryReadFb(out baselineFb))
            {
                Volatile.Write(ref busy, 0);
                return false;
            }

            this.timing = timing;
            Interlocked.Exchange(ref swapTime, 0);
            wake.Release();
            return true;
        }

        /// <summary>
        /// Draw thread, right before swapping any timed frame, so a watched frame can be told when the next one overtakes it.
        /// </summary>
        public void NoteSwap(long ns) => Interlocked.Exchange(ref latestSwapTime, ns);

        /// <summary>
        /// Draw thread, right before swapping the frame <see cref="TryArm"/> was called for, and after <see cref="NoteSwap"/>.
        /// </summary>
        public void MarkSwap(long ns) => Interlocked.Exchange(ref swapTime, ns);

        private void run()
        {
            Native.SetTimerSlack(1);

            while (true)
            {
                wake.Wait();

                if (disposed)
                    break;

                watch();
                Volatile.Write(ref busy, 0);
            }

            Native.close(fd);
        }

        private void watch()
        {
            long armed = Native.MonotonicNs();
            long previousRead = 0;

            while (true)
            {
                if (!tryReadFb(out uint fb))
                    return;

                long now = Native.MonotonicNs();
                long swap = Interlocked.Read(ref swapTime);

                if (fb != baselineFb)
                {
                    if (swap == 0)
                    {
                        // An earlier frame flipped while this one waited for its scanline. This one's flip is still to come.
                        baselineFb = fb;
                    }
                    else
                    {
                        // The flip happened between the last two reads. Unless the earlier one came after the swap,
                        // the probe was not running when it happened, and the time is unknown.
                        if (previousRead >= swap)
                        {
                            long flip = (previousRead + now) / 2;
                            long latestSwap = Interlocked.Read(ref latestSwapTime);

                            if (latestSwap > swap && latestSwap < flip)
                                onOvertaken(timing);
                            else
                                onFlip(timing, flip - swap);
                        }

                        return;
                    }
                }
                else if (swap != 0 ? now - swap > timeout_ns : now - armed > swap_timeout_ns)
                {
                    if (swap != 0)
                        onMiss(timing);

                    return;
                }

                previousRead = now;
                Thread.SpinWait(20);
            }
        }

        private bool tryReadFb(out uint fb)
        {
            Native.DrmModeCrtc crtc = new Native.DrmModeCrtc { CrtcId = CrtcId };

            if (Native.ioctl(fd, Native.DRM_IOCTL_MODE_GETCRTC, &crtc) != 0)
            {
                fb = 0;
                return false;
            }

            fb = crtc.FbId;
            return true;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            // The probe thread closes the device once it is done with any flip it is watching for.
            disposed = true;

            if (fd >= 0)
                wake.Release();
        }
    }
}

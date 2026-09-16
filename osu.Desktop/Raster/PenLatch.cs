// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Platform.Pointer;
using osu.Framework.Bindables;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.Handlers.Tablet;
using osu.Framework.Logging;
using osu.Game.Graphics.Raster;
using Vector2 = osuTK.Vector2;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Sees each of the tablet's reports as it arrives, on its way to the tablet handler, so the gameplay cursor can be drawn at the newest one.
    /// </summary>
    /// <remarks>
    /// A report reaches the screen through the handler's input queue, the next update frame, the draw after that and the render margin.
    /// The cursor is the one thing whose position the draw thread can correct on its own: it moves the cursor by however far the pen has
    /// travelled since the update frame placed it. Hits are still judged where the update frame had the cursor.
    ///
    /// It sits between OpenTabletDriver's output mode and the handler rather than replacing the handler, because the input settings are
    /// saved under the handler's type and a subclass would lose the tablet area.
    /// </remarks>
    internal sealed class PenLatch : IPointerLatch, IAbsolutePointer, IPressureHandler
    {
        /// <summary>
        /// Off with <c>OSU_POINTER_LATCH=0</c>, which draws the cursor where the update frame put it.
        /// </summary>
        public static readonly bool ENABLED = Environment.GetEnvironmentVariable(@"OSU_POINTER_LATCH") != @"0";

        /// <summary>
        /// Recent reports a cursor can sit on to count as following the pen. Must be a power of two.
        /// </summary>
        private const int history = 16;

        /// <summary>
        /// How far from a report a cursor may sit and still be on it, squared, which only has to absorb rounding between spaces.
        /// </summary>
        private const float match_distance_squared = 0.25f;

        private static readonly FieldInfo? output_mode_field = typeof(OpenTabletDriverHandler).GetField(@"outputMode", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly OpenTabletDriverHandler handler;
        private readonly IAbsolutePointer pointer;
        private readonly IPressureHandler pressure;
        private readonly IBindable<TabletInfo?> tablet;

        // Written on the tablet's thread, read on the update and draw threads. A position packs into one long so it is read whole.
        private readonly long[] reports = new long[history];
        private int nextReport;
        private long latest;

#if RASTER_METRICS
        private const int metric_history = 1024;

        // Offsets are recorded in hundred-thousandths of a pixel, so the window's 10 µs bins are tenths of a pixel.
        private const float offset_scale = 100_000;

        private long latestAt;
        private long lastReportAt;
        private int intervalReports;
        private readonly DurationWindow reportIntervals = new DurationWindow(metric_history);

        private int intervalFollowing;
        private int intervalNotFollowing;

        // Draw thread.
        private readonly DurationWindow offsets = new DurationWindow(metric_history);
        private readonly DurationWindow reportAges = new DurationWindow(metric_history);
        private readonly DurationWindow latchToPresent = new DurationWindow(metric_history);
        private long takenAt;
        private int intervalTaken;
        private float intervalMaxOffset;
#endif

        private PenLatch(OpenTabletDriverHandler handler)
        {
            this.handler = handler;
            pointer = handler;
            pressure = handler;

            // The handler makes a new output mode each time a tablet is detected, which happens on OpenTabletDriver's own thread
            // well after the handlers are initialised, and again whenever the tablet is reconnected. The tablet it reports is set
            // just after, so that is when to stand in front of the new one.
            tablet = handler.Tablet.GetBoundCopy();
            tablet.BindValueChanged(t =>
            {
                if (t.NewValue != null)
                    attach(t.NewValue);
            }, true);
        }

        private void attach(TabletInfo info)
        {
            if (output_mode_field?.GetValue(handler) is not AbsoluteOutputMode outputMode)
            {
                Logger.Log($@"Pen latch: {info.Name} has no absolute output mode, so the cursor is drawn where update frames put it.");
                return;
            }

            if (outputMode.Pointer == this)
                return;

            if (outputMode.Pointer != handler)
            {
                Logger.Log($@"Pen latch: {info.Name} reports to something other than the tablet handler, so the cursor is drawn where update frames put it.");
                return;
            }

            outputMode.Pointer = this;
            Logger.Log($@"Pen latch: following {info.Name}.");
        }

        /// <summary>
        /// Puts a latch between OpenTabletDriver and the tablet handler, once the handlers have been initialised. Returns null if it cannot.
        /// </summary>
        public static PenLatch? TryInstall(IEnumerable<InputHandler> handlers)
        {
            if (!ENABLED)
                return null;

            var handler = handlers.OfType<OpenTabletDriverHandler>().SingleOrDefault();

            if (handler == null)
                return null;

            // OpenTabletDriver looks for these on the pointer it reports to. Standing in for a handler that implements more
            // than the latch forwards would silently drop them, so a framework that adds one leaves the latch out.
            var pointerInterfaces = handler.GetType().GetInterfaces().Where(i => i.Namespace == typeof(IAbsolutePointer).Namespace).ToArray();
            var forwarded = new[] { typeof(IAbsolutePointer), typeof(IRelativePointer), typeof(IPressureHandler) };

            if (pointerInterfaces.Except(forwarded).Any() || output_mode_field?.FieldType != typeof(AbsoluteOutputMode))
            {
                Logger.Log(@"Pen latch: the tablet handler is not laid out as expected, so the cursor is drawn where update frames put it.");
                return null;
            }

            return new PenLatch(handler);
        }

        void IAbsolutePointer.SetPosition(System.Numerics.Vector2 pos)
        {
            long packed = pack(pos.X, pos.Y);

            // Recorded before the handler queues it, so an update frame never has the cursor on a report the latch has not seen.
            reports[nextReport & (history - 1)] = packed;
            Volatile.Write(ref nextReport, nextReport + 1);
            Volatile.Write(ref latest, packed);

#if RASTER_METRICS
            long now = Native.MonotonicNs();

            if (lastReportAt > 0)
                reportIntervals.Add(now - lastReportAt);

            lastReportAt = now;
            Volatile.Write(ref latestAt, now);
            Interlocked.Increment(ref intervalReports);
#endif

            pointer.SetPosition(pos);
        }

        void IPressureHandler.SetPressure(float percentage) => pressure.SetPressure(percentage);

        public bool IsFollowingPen(Vector2 screenSpacePosition)
        {
            int written = Math.Min(Volatile.Read(ref nextReport), history);

            for (int i = 0; i < written; i++)
            {
                long packed = Volatile.Read(ref reports[i]);

                if (Vector2.DistanceSquared(unpack(packed), screenSpacePosition) <= match_distance_squared)
                {
#if RASTER_METRICS
                    Interlocked.Increment(ref intervalFollowing);
#endif
                    return true;
                }
            }

#if RASTER_METRICS
            Interlocked.Increment(ref intervalNotFollowing);
#endif
            return false;
        }

        public Vector2 TakeOffset(Vector2 screenSpacePosition)
        {
            Vector2 offset = unpack(Volatile.Read(ref latest)) - screenSpacePosition;

#if RASTER_METRICS
            takenAt = Native.MonotonicNs();
            reportAges.Add(takenAt - Volatile.Read(ref latestAt));

            float length = offset.Length;

            offsets.Add((long)(length * offset_scale));
            intervalMaxOffset = Math.Max(intervalMaxOffset, length);
            intervalTaken++;
#endif

            return offset;
        }

#if RASTER_METRICS
        /// <summary>
        /// Draw thread, when a frame's present starts.
        /// </summary>
        public void NotePresent(long presentStart)
        {
            if (takenAt == 0)
                return;

            latchToPresent.Add(presentStart - takenAt);
            takenAt = 0;
        }

        /// <summary>
        /// A log line for the interval just ended, which starts the next. Draw thread.
        /// </summary>
        public string TakeIntervalSummary()
        {
            int reportCount = Interlocked.Exchange(ref intervalReports, 0);
            int following = Interlocked.Exchange(ref intervalFollowing, 0);
            int notFollowing = Interlocked.Exchange(ref intervalNotFollowing, 0);

            string summary = $"Raster sync cursor: {reportCount} pen reports, {ms(reportIntervals.Percentile(0.5))}/{ms(reportIntervals.Percentile(0.99))} ms apart at p50/p99. "
                             + $"Update frames had the cursor on the pen {following} times and off it {notFollowing}, and {intervalTaken} draws moved it to the newest report, "
                             + $"by {offsets.Percentile(0.5) / offset_scale:0.0}/{offsets.Percentile(0.99) / offset_scale:0.0}/{intervalMaxOffset:0.0} px at p50/p99/max. "
                             + $"Milliseconds at p50/p99: the newest report was {ms(reportAges.Percentile(0.5))}/{ms(reportAges.Percentile(0.99))} old when drawn, "
                             + $"and presented {ms(latchToPresent.Percentile(0.5))}/{ms(latchToPresent.Percentile(0.99))} later.";

            intervalTaken = 0;
            intervalMaxOffset = 0;

            return summary;
        }

        private static string ms(long ns) => $"{ns / 1e6:0.000}";
#endif

        private static long pack(float x, float y) => (long)((ulong)(uint)BitConverter.SingleToInt32Bits(x) << 32 | (uint)BitConverter.SingleToInt32Bits(y));

        private static Vector2 unpack(long packed) => new Vector2(BitConverter.Int32BitsToSingle((int)(packed >> 32)), BitConverter.Int32BitsToSingle((int)packed));
    }
}

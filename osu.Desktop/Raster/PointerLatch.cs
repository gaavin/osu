// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Input.Handlers;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Graphics.Raster;
using Vector2 = osuTK.Vector2;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Sees each of the pointer's reports as it arrives, on its way to the handler that queues it, so the gameplay cursor can be drawn at the newest one.
    /// </summary>
    /// <remarks>
    /// A report reaches the screen through a handler's input queue, the next update frame, the draw after that and the render margin.
    /// The cursor is the one thing whose position the draw thread can correct on its own: it moves the cursor by however far the pointer has
    /// travelled since the update frame placed it. Hits are still judged where the update frame had the cursor.
    ///
    /// Each device the cursor can follow is a <see cref="PointerSource"/> of its own: the pen through <see cref="PenSource"/>, the mouse
    /// through <see cref="MouseSource"/>. The cursor follows whichever one the update frame left it on, so a pen resting in proximity never
    /// pulls a cursor the mouse is driving, and the tablet can be picked up mid-play.
    /// </remarks>
    internal sealed class PointerLatch : IPointerLatch
    {
        /// <summary>
        /// Off with <c>OSU_POINTER_LATCH=0</c>, which draws the cursor where the update frame put it.
        /// </summary>
        public static readonly bool ENABLED = Environment.GetEnvironmentVariable(@"OSU_POINTER_LATCH") != @"0";

        /// <summary>
        /// Whether a cursor in a frame timed against the display is drawn after the rest of the frame has finished on the GPU, rather than where the scene draws it.
        /// <c>OSU_POINTER_LATCH=draw</c> keeps it in the scene, moved to the newest report as of the scene's draw.
        /// </summary>
        public static readonly bool LATE = Environment.GetEnvironmentVariable(@"OSU_POINTER_LATCH") != @"draw";

        // Devices are found on their own threads, well after the update and draw threads are reading this, so it is replaced whole rather than added to.
        private volatile PointerSource[] sources = Array.Empty<PointerSource>();

        private readonly object sourceLock = new object();

        private PenSource? pen;

        // Written on the update thread, read on the draw thread.
        private volatile PointerSource? followed;
        private float cursorExtentAbove;

        // Draw thread only.
        private bool defersDraws;
        private ILatchedDraw? deferred;

        /// <summary>
        /// Whether any device is followed, or may yet be: without one the cursor is drawn where update frames put it.
        /// </summary>
        public bool FollowsAnything => sources.Length > 0 || pen != null;

        /// <summary>
        /// Follows the mouse, whose reports the window hands the mouse handler. As the window is made, before the input handlers subscribe to it.
        /// </summary>
        public void FollowMouse(IWindow window) => MouseSource.TryFollow(window, this);

        /// <summary>
        /// Follows the tablet, whose reports OpenTabletDriver hands the tablet handler. Once the input handlers have been initialised.
        /// </summary>
        public void FollowTablet(IEnumerable<InputHandler> handlers) => pen ??= PenSource.TryFollow(handlers, this);

        /// <summary>
        /// Starts recording a device's reports, once it is there to record. Its own thread.
        /// </summary>
        public void Add(PointerSource source)
        {
            lock (sourceLock)
            {
                if (sources.Contains(source))
                    return;

                sources = sources.Append(source).ToArray();
            }

            Logger.Log($@"Pointer latch: following the {source.Name}.");
        }

        public bool IsFollowingPointer(Vector2 screenSpacePosition, float extentAbove)
        {
            var source = findSource(screenSpacePosition);

            if (source != null)
                Volatile.Write(ref cursorExtentAbove, extentAbove);

            followed = source;

#if RASTER_METRICS
            if (source != null)
                Interlocked.Increment(ref intervalFollowing);
            else
                Interlocked.Increment(ref intervalNotFollowing);
#endif

            return source != null;
        }

        private PointerSource? findSource(Vector2 screenSpacePosition)
        {
            // Whichever device the cursor was on is the one it is on, every frame but the one another device takes it over in.
            var last = followed;

            if (last != null && last.IsOn(screenSpacePosition))
                return last;

            foreach (var source in sources)
            {
                if (source != last && source.IsOn(screenSpacePosition))
                    return source;
            }

            return null;
        }

        /// <summary>
        /// The screen row the top of the cursor is at by the newest report, if the latest update frame had the cursor following a pointer. Draw thread.
        /// </summary>
        public bool TryGetCursorTop(out float row)
        {
            var source = followed;

            row = (source?.Latest.Y ?? 0) - Volatile.Read(ref cursorExtentAbove);
            return source != null;
        }

        public Vector2 TakeOffset(Vector2 screenSpacePosition)
        {
            var source = followed;

            if (source == null)
                return Vector2.Zero;

            Vector2 offset = source.Latest - screenSpacePosition;

#if RASTER_METRICS
            takenAt = Native.MonotonicNs();
            takenLate = drawingDeferred;
            reportAges.Add(takenAt - source.LatestAt);

            float length = offset.Length;

            offsets.Add((long)(length * offset_scale));
            intervalMaxOffset = Math.Max(intervalMaxOffset, length);
            intervalTaken++;
#endif

            return offset;
        }

        public bool DefersDraws => defersDraws;

        public bool TryDeferDraw(ILatchedDraw draw)
        {
            if (!defersDraws || deferred != null)
                return false;

            deferred = draw;
            return true;
        }

        /// <summary>
        /// Draw thread, before a frame is drawn: whether its present is the one timed to tear just above the cursor, which is the only one a held draw is worth it for.
        /// </summary>
        public void BeginFrame(bool timedForCursor)
        {
            defersDraws = timedForCursor && LATE;
            deferred = null;
        }

        public bool HasDeferredDraw => deferred != null;

        /// <summary>
        /// Draws what was held, on top of the frame. Draw thread, once the rest of the frame has finished on the GPU.
        /// </summary>
        public void DrawDeferred(IRenderer renderer)
        {
            var draw = deferred;

            deferred = null;
            defersDraws = false;

#if RASTER_METRICS
            drawingDeferred = true;
#endif
            draw?.DrawLatched(renderer);
#if RASTER_METRICS
            drawingDeferred = false;
#endif
        }

#if RASTER_METRICS
        private const int metric_history = 1024;

        // Offsets are recorded in hundred-thousandths of a pixel, so the window's 10 µs bins are tenths of a pixel.
        private const float offset_scale = 100_000;

        private int intervalFollowing;
        private int intervalNotFollowing;

        // Draw thread.
        private readonly DurationWindow offsets = new DurationWindow(metric_history);
        private readonly DurationWindow reportAges = new DurationWindow(metric_history);
        private readonly DurationWindow latchToPresent = new DurationWindow(metric_history);

        /// <summary>
        /// The part of <see cref="latchToPresent"/> taken by cursors drawn after the rest of the frame.
        /// </summary>
        private readonly DurationWindow lateLatchToPresent = new DurationWindow(metric_history);

        private long takenAt;
        private bool takenLate;
        private bool drawingDeferred;
        private int intervalTaken;
        private float intervalMaxOffset;

        /// <summary>
        /// Draw thread, when a frame's present starts.
        /// </summary>
        public void NotePresent(long presentStart)
        {
            if (takenAt == 0)
                return;

            latchToPresent.Add(presentStart - takenAt);

            if (takenLate)
                lateLatchToPresent.Add(presentStart - takenAt);

            takenAt = 0;
        }

        /// <summary>
        /// A log line for the interval just ended, which starts the next. Draw thread.
        /// </summary>
        public string TakeIntervalSummary()
        {
            int following = Interlocked.Exchange(ref intervalFollowing, 0);
            int notFollowing = Interlocked.Exchange(ref intervalNotFollowing, 0);
            string reports = sources.Length == 0 ? @"no pointer to follow" : string.Join(@", ", sources.Select(s => s.TakeIntervalSummary()));

            string summary = $"Raster sync cursor: {reports}. "
                             + $"Update frames had the cursor on a pointer {following} times and off it {notFollowing}, and {intervalTaken} draws moved it to the newest report, "
                             + $"by {offsets.Percentile(0.5) / offset_scale:0.0}/{offsets.Percentile(0.99) / offset_scale:0.0}/{intervalMaxOffset:0.0} px at p50/p99/max. "
                             + $"Milliseconds at p50/p99: the newest report was {PointerSource.Ms(reportAges.Percentile(0.5))}/{PointerSource.Ms(reportAges.Percentile(0.99))} old when drawn, "
                             + $"and presented {PointerSource.Ms(latchToPresent.Percentile(0.5))}/{PointerSource.Ms(latchToPresent.Percentile(0.99))} later, "
                             + $"{PointerSource.Ms(lateLatchToPresent.Percentile(0.5))}/{PointerSource.Ms(lateLatchToPresent.Percentile(0.99))} for cursors drawn after the rest of the frame.";

            intervalTaken = 0;
            intervalMaxOffset = 0;

            return summary;
        }
#endif
    }
}

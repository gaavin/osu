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

        /// <summary>
        /// How long before its row is scanned out a timed device's path is sampled for the cursor, or null to draw it at the newest report.
        /// <c>OSU_POINTER_RESAMPLE</c> sets it in milliseconds, or <c>off</c>. At 0 the path is predicted up to scanout, from the velocity over the last couple of reports;
        /// at a report interval or more (about 1 on the CTL-480) it is only ever interpolated between reports, and the cursor is that much older.
        /// </summary>
        /// <remarks>
        /// The pen reports about once a millisecond on its own clock while the display scans out on another, so drawn at the newest report the cursor is
        /// between nothing and a report interval old at scanout, differently every refresh, and wobbles along its path. Moving the present cannot fix that:
        /// with a fixed refresh the cursor's row is scanned out at the same point of every refresh whenever the frame was presented. Sampling the path
        /// at a fixed time before that point makes every refresh's cursor the same age.
        /// </remarks>
        public static readonly long? RESAMPLE_LEAD_NS = parseResampleLead(Environment.GetEnvironmentVariable(@"OSU_POINTER_RESAMPLE"));

        private static long? parseResampleLead(string? value)
        {
            if (value == @"off")
                return null;

            return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ms) && ms >= 0
                ? (long)(ms * 1_000_000)
                : 0;
        }

        /// <summary>
        /// When a frame's tear line is scanned out, and how the display scans, so the time a row of it is scanned out can be worked out.
        /// </summary>
        public readonly record struct Scanout(long TearNs, long VBlankNs, long PeriodNs, int VDisplay, int VTotal)
        {
            /// <summary>
            /// When a row is first scanned out from this frame's tear line on.
            /// </summary>
            public long RowScannedAt(float row)
            {
                long rowAt = VBlankNs + (long)(Math.Clamp(row, 0, VDisplay - 1) * PeriodNs / VTotal);

                return rowAt + (long)Math.Ceiling((double)(TearNs - rowAt) / PeriodNs) * PeriodNs;
            }
        }

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
        private Scanout? scanout;

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

        /// <summary>
        /// The screen row the top of the cursor will be at, by where the pointer's path puts it at a time: predicted past the newest report,
        /// sampled <see cref="RESAMPLE_LEAD_NS"/> before that time as the cursor itself is. False if the followed device's reports do not say. Draw thread.
        /// </summary>
        public bool TryGetCursorTopAt(long time, out float row)
        {
            var source = followed;

            row = 0;

            if (source == null || RESAMPLE_LEAD_NS is not long lead || !source.TryGetPositionAt(time - lead, out Vector2 position, out _))
                return false;

            row = position.Y - Volatile.Read(ref cursorExtentAbove);
            return true;
        }

        public Vector2 TakeOffset(Vector2 screenSpacePosition)
        {
            var source = followed;

            if (source == null)
                return Vector2.Zero;

            Vector2 latest = source.Latest;
            Vector2 drawn = latest;

            if (RESAMPLE_LEAD_NS is long lead && scanout is Scanout frame)
            {
                long sampleAt = frame.RowScannedAt(latest.Y) - lead;

                if (source.TryGetPositionAt(sampleAt, out drawn, out long newestAt))
                {
#if RASTER_METRICS
                    long ahead = sampleAt - newestAt;

                    if (ahead >= 0)
                    {
                        intervalPredicted++;
                        predictionHorizons.Add(ahead);
                    }
                    else
                        intervalInterpolated++;

                    resampleShifts.Add((long)((drawn - latest).Length * offset_scale));
#endif
                }
            }

            Vector2 offset = drawn - screenSpacePosition;

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
        /// <param name="timedForCursor">Whether the present is the one timed to tear just above the cursor.</param>
        /// <param name="frameScanout">When the frame's tear line is planned to be scanned out, if its present is timed, which the cursor is sampled against.</param>
        public void BeginFrame(bool timedForCursor, Scanout? frameScanout = null)
        {
            defersDraws = timedForCursor && LATE;
            deferred = null;
            scanout = frameScanout;
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

        private readonly DurationWindow predictionHorizons = new DurationWindow(metric_history);
        private readonly DurationWindow resampleShifts = new DurationWindow(metric_history);
        private int intervalPredicted;
        private int intervalInterpolated;

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
                             + $"{PointerSource.Ms(lateLatchToPresent.Percentile(0.5))}/{PointerSource.Ms(lateLatchToPresent.Percentile(0.99))} for cursors drawn after the rest of the frame. "
                             + (RESAMPLE_LEAD_NS is long lead
                                 ? $"Sampled {PointerSource.Ms(lead)} ms before scanout: {intervalPredicted} predicted past the newest report by {PointerSource.Ms(predictionHorizons.Percentile(0.5))}/{PointerSource.Ms(predictionHorizons.Percentile(0.99))} ms, "
                                   + $"{intervalInterpolated} between reports, moving the cursor {resampleShifts.Percentile(0.5) / offset_scale:0.0}/{resampleShifts.Percentile(0.99) / offset_scale:0.0} px from the newest report at p50/p99."
                                 : "Drawn at the newest report.");

            intervalPredicted = 0;
            intervalInterpolated = 0;

            intervalTaken = 0;
            intervalMaxOffset = 0;

            return summary;
        }
#endif
    }
}

// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using Vector2 = osuTK.Vector2;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// One device the gameplay cursor can be drawn at the newest report of: where it last reported, and enough of what came before
    /// to tell whether a cursor is following it.
    /// </summary>
    /// <remarks>
    /// Reports are recorded as they arrive, on the device's own thread, before whatever queues them for the update thread sees them,
    /// so an update frame never has the cursor on a report the latch has not seen.
    /// </remarks>
    internal abstract class PointerSource
    {
        /// <summary>
        /// Recent reports a cursor can sit on to count as following the device. Must be a power of two.
        /// </summary>
        private const int history = 16;

        /// <summary>
        /// How far from a report a cursor may sit and still be on it, squared, which only has to absorb rounding between spaces.
        /// </summary>
        private const float match_distance_squared = 0.25f;

        /// <summary>
        /// What the device is called in the log.
        /// </summary>
        public readonly string Name;

        // Written on the device's thread, read on the update and draw threads. A position packs into one long so it is read whole.
        private readonly long[] reports = new long[history];
        private int nextReport;
        private long latest;

        protected PointerSource(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Records a report. The device's thread, before the report is queued for the update thread.
        /// </summary>
        protected void Report(float x, float y)
        {
            long packed = pack(x, y);

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
        }

        /// <summary>
        /// Whether a cursor at this screen space position is on one of the device's recent reports, which is what it means for the cursor to be following it. Update thread.
        /// </summary>
        public bool IsOn(Vector2 screenSpacePosition)
        {
            int written = Math.Min(Volatile.Read(ref nextReport), history);

            for (int i = 0; i < written; i++)
            {
                if (Vector2.DistanceSquared(unpack(Volatile.Read(ref reports[i])), screenSpacePosition) <= match_distance_squared)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Where the device last reported. Draw thread.
        /// </summary>
        public Vector2 Latest => unpack(Volatile.Read(ref latest));

#if RASTER_METRICS
        private const int metric_history = 1024;

        private long latestAt;
        private long lastReportAt;
        private int intervalReports;
        private readonly DurationWindow reportIntervals = new DurationWindow(metric_history);

        /// <summary>
        /// When the device last reported. Draw thread.
        /// </summary>
        public long LatestAt => Volatile.Read(ref latestAt);

        /// <summary>
        /// The device's part of a log line for the interval just ended, which starts the next. Draw thread.
        /// </summary>
        public string TakeIntervalSummary()
        {
            int reportCount = Interlocked.Exchange(ref intervalReports, 0);

            return reportCount == 0
                ? $"no {Name} reports"
                : $"{reportCount} {Name} reports, {Ms(reportIntervals.Percentile(0.5))}/{Ms(reportIntervals.Percentile(0.99))} ms apart at p50/p99";
        }

        public static string Ms(long ns) => $"{ns / 1e6:0.000}";
#endif

        private static long pack(float x, float y) => (long)((ulong)(uint)BitConverter.SingleToInt32Bits(x) << 32 | (uint)BitConverter.SingleToInt32Bits(y));

        private static Vector2 unpack(long packed) => new Vector2(BitConverter.Int32BitsToSingle((int)(packed >> 32)), BitConverter.Int32BitsToSingle((int)packed));
    }
}

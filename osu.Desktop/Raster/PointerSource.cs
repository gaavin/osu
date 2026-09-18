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
        /// How far back a report may be to measure the pointer's velocity from, when predicting past the newest one.
        /// A baseline of a couple of reports keeps a pair that arrived together, or a repeated position, from swinging the prediction.
        /// </summary>
        private const long velocity_baseline_ns = 2_000_000;

        /// <summary>
        /// Reports further apart than this mean the pointer stopped reporting, as a pen lifted out of range does, and nothing is predicted across the gap.
        /// </summary>
        private const long stale_gap_ns = 8_000_000;

        /// <summary>
        /// The furthest past the newest report the pointer is predicted.
        /// </summary>
        private const long max_prediction_ns = 3_000_000;

        /// <summary>
        /// Reports read back at most, leaving slots the device's thread can write while the draw thread reads without catching up with it.
        /// </summary>
        private const int readable = history - 4;

        /// <summary>
        /// What the device is called in the log.
        /// </summary>
        public readonly string Name;

        /// <summary>
        /// Whether the device's reports arrive when it sends them, so the time each arrives says where the pointer was when.
        /// A mouse's are handed over as the window thread polls, bunched, and are drawn at the newest instead.
        /// </summary>
        public readonly bool Timed;

        // Written on the device's thread, read on the update and draw threads. A position packs into one long so it is read whole.
        private readonly long[] reports = new long[history];
        private readonly long[] reportTimes = new long[history];
        private int nextReport;
        private long latest;

        protected PointerSource(string name, bool timed)
        {
            Name = name;
            Timed = timed;
        }

        /// <summary>
        /// Records a report. The device's thread, before the report is queued for the update thread.
        /// </summary>
        protected void Report(float x, float y)
        {
            long packed = pack(x, y);
            long now = Native.MonotonicNs();

            reports[nextReport & (history - 1)] = packed;
            reportTimes[nextReport & (history - 1)] = now;
            Volatile.Write(ref nextReport, nextReport + 1);
            Volatile.Write(ref latest, packed);

#if RASTER_METRICS

            if (lastReportAt > 0)
                reportIntervals.Add(now - lastReportAt);

            lastReportAt = now;
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

        /// <summary>
        /// Where the pointer was at a time, between the reports either side of it, or predicted from the last few past the newest. Draw thread.
        /// Returns false if the reports do not say, as for a device that is not <see cref="Timed"/>, and gives the newest report instead.
        /// </summary>
        /// <remarks>
        /// The display scans out on its own clock and the device reports on another, so the newest report is anywhere from nothing to a whole
        /// report interval old when the cursor is scanned out, and that changes from one refresh to the next. At speed that is a cursor which wobbles
        /// along its path by however far the pointer moves in one interval. Sampling the path at a fixed time before scanout keeps it the same age every refresh.
        /// </remarks>
        public bool TryGetPositionAt(long time, out Vector2 position, out long newestAt)
        {
            int written = Volatile.Read(ref nextReport);

            position = Latest;
            newestAt = 0;

            if (!Timed || written < 2)
                return false;

            int count = Math.Min(written, readable);
            int newestSlot = (written - 1) & (history - 1);

            newestAt = reportTimes[newestSlot];
            Vector2 newest = unpack(reports[newestSlot]);

            if (time >= newestAt)
            {
                // Past the newest report: carried on at the velocity over the last couple of reports, unless the device has gone quiet.
                if (time - newestAt > stale_gap_ns)
                    count = 1;

                long horizon = Math.Min(time - newestAt, max_prediction_ns);
                int baseline = -1;

                for (int i = 1; i < count; i++)
                {
                    int slot = (written - 1 - i) & (history - 1);
                    long gap = newestAt - reportTimes[slot];

                    if (gap > stale_gap_ns)
                        break;

                    baseline = slot;

                    if (gap >= velocity_baseline_ns)
                        break;
                }

                position = newest;

                if (baseline >= 0 && reportTimes[baseline] < newestAt)
                {
                    Vector2 velocity = (newest - unpack(reports[baseline])) / (newestAt - reportTimes[baseline]);

                    position = newest + velocity * horizon;
                }
            }
            else
            {
                // Between two reports: where the pointer was on the straight line joining them.
                position = newest;

                long laterAt = newestAt;
                Vector2 later = newest;

                for (int i = 1; i < count; i++)
                {
                    int slot = (written - 1 - i) & (history - 1);
                    long earlierAt = reportTimes[slot];
                    Vector2 earlier = unpack(reports[slot]);

                    position = earlier;

                    if (earlierAt <= time)
                    {
                        if (laterAt > earlierAt && laterAt - earlierAt <= stale_gap_ns)
                            position = Vector2.Lerp(earlier, later, (float)(time - earlierAt) / (laterAt - earlierAt));

                        break;
                    }

                    laterAt = earlierAt;
                    later = earlier;
                }
            }

            // The device's thread writing over what was just read means it was read mid-write, and the newest report is safer.
            if (Volatile.Read(ref nextReport) - written >= history - readable)
            {
                position = Latest;
                return false;
            }

            return true;
        }

#if RASTER_METRICS
        private const int metric_history = 1024;

        private long lastReportAt;
        private int intervalReports;
        private readonly DurationWindow reportIntervals = new DurationWindow(metric_history);

        /// <summary>
        /// When the device last reported. Draw thread.
        /// </summary>
        public long LatestAt => reportTimes[(Volatile.Read(ref nextReport) - 1) & (history - 1)];

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

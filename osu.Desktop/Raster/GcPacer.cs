// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Collects garbage at a moment raster sync picks, rather than wherever allocation happens to reach the budget.
    /// </summary>
    /// <remarks>
    /// A collection costs osu! about the same whether it has a little garbage or a lot: measured in a play, a 219 KiB
    /// nursery took 0.61 ms where a 16 MB one takes a few milliseconds, because most of the time goes on roots and card
    /// tables rather than on the garbage itself. Collecting more often to keep each one short therefore costs far more
    /// in total, which is why this does not change how often collections happen. It moves the collection that was going
    /// to happen anyway into a gap reserved for it, before a frame starts drawing, where it delays nothing that is
    /// already on its way to the screen.
    ///
    /// A collection lasts longer than the gap between presents, so the present it precedes is aimed a slice or two
    /// further down the screen to make room. That gives up a slice about every five seconds, against a collection that
    /// would otherwise land in the middle of a draw, or while a finished frame waits for its scanline.
    /// </remarks>
    internal sealed class GcPacer
    {
        /// <summary>
        /// How much of the budget has to be spent before a collection is forced. The runtime collects at the whole
        /// budget, so this has to be short enough of it to get there first, and close enough not to waste the nursery.
        /// </summary>
        private const double trigger_fraction = 0.9;

        /// <summary>
        /// What to set aside for a collection before one has been timed here.
        /// </summary>
        private const long assumed_pause_ns = 4_000_000;

        /// <summary>
        /// The most a present will give up for a collection, so that one slow outlier cannot stall pacing for a refresh.
        /// </summary>
        private const long max_reserve_ns = 8_000_000;

        private const int pause_history = 64;

        private readonly DurationWindow pauses = new DurationWindow(pause_history);
        private readonly bool enabled;

        private int timedPauses;
        private long budgetBytes;
        private long collectedAtBytes;
        private long reservedNs;
        private bool lastWasForced;
        private int forced;

        public GcPacer()
        {
            string? setting = Environment.GetEnvironmentVariable(@"OSU_RASTER_GC_PACING");

            enabled = setting != @"0" && !@"false".Equals(setting, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The budget the runtime appears to be collecting at, as far as it has been watched.
        /// </summary>
        public long BudgetBytes => budgetBytes;

        /// <summary>
        /// What a collection is expected to take, and so what a present gives up to hold one.
        /// </summary>
        /// <remarks>
        /// The median rather than a high percentile. Reserving for the worst collection gives up slices on every one of
        /// them — measured at 3.3 slices a collection where one was meant — while reserving for the typical collection
        /// costs nothing at all until one runs long, and then only starts that single frame late.
        /// </remarks>
        public long ExpectedPauseNs => Math.Min(max_reserve_ns, timedPauses > 0 ? pauses.Percentile(0.5) : assumed_pause_ns);

        /// <summary>
        /// The collections forced since this was last read.
        /// </summary>
        public int TakeForced()
        {
            int count = forced;
            forced = 0;
            return count;
        }

        /// <summary>
        /// How long the next present should set aside before it starts drawing, or zero if no collection is due.
        /// Draw thread, before the present is planned.
        /// </summary>
        public long Reserve()
        {
            if (!enabled || budgetBytes <= 0)
                return 0;

            // Reading the allocated total costs a couple of nanoseconds and counts every thread, which matters
            // because nearly all of the allocation is the update thread's rather than this one's.
            if (GC.GetTotalAllocatedBytes() - collectedAtBytes < (long)(budgetBytes * trigger_fraction))
                return 0;

            return reservedNs = ExpectedPauseNs;
        }

        /// <summary>
        /// Collects in the gap <see cref="Reserve"/> asked for. Draw thread, after the present is planned and before
        /// the thread sleeps for it.
        /// </summary>
        public void CollectIfReserved()
        {
            if (reservedNs == 0)
                return;

            reservedNs = 0;
            lastWasForced = true;
            forced++;

            // Gen0 alone, and without compacting: the point is to spend what the runtime was about to spend anyway at
            // a time of this frame's choosing, not to do more work than it would have.
            GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: false);

            // The baseline is reset here rather than left to NoteCollection, which can decline a collection it cannot
            // match to an index. Were that to happen with the nursery still reading as full, every present would collect.
            collectedAtBytes = GC.GetTotalAllocatedBytes();
        }

        /// <summary>
        /// Notes a collection the runtime has done, forced here or not, to learn what one costs and how much
        /// allocation earns one. Draw thread.
        /// </summary>
        public void NoteCollection(long allocatedTotal, long bytesSincePrevious, long pauseNs)
        {
            if (pauseNs > 0)
            {
                pauses.Add(pauseNs);
                timedPauses++;
            }

            // A forced collection only says how much was allocated before this decided to collect, which would drag the
            // estimate down a tenth at a time until collections were constant. Only the runtime's own timing measures it.
            if (lastWasForced)
                lastWasForced = false;
            else if (bytesSincePrevious > 0)
                budgetBytes = budgetBytes == 0 ? bytesSincePrevious : (budgetBytes * 7 + bytesSincePrevious) / 8;

            collectedAtBytes = allocatedTotal;
        }
    }
}

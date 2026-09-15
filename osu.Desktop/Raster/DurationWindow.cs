// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// The durations of the most recent presents, with a percentile cheap enough to read before every present.
    /// </summary>
    internal sealed class DurationWindow
    {
        private const long bin_ns = 10_000;

        /// <summary>
        /// Durations past 20 ms count towards the last bin.
        /// </summary>
        private const int bin_count = 2_000;

        private readonly long[] samples;
        private readonly int[] bins = new int[bin_count];

        private int next;
        private int count;

        public DurationWindow(int size)
        {
            samples = new long[size];
        }

        public void Add(long ns)
        {
            if (count == samples.Length)
                bins[binOf(samples[next])]--;
            else
                count++;

            samples[next] = ns;
            bins[binOf(ns)]++;
            next = (next + 1) % samples.Length;
        }

        /// <summary>
        /// The duration that the given fraction of recent presents took at most, rounded up to the next 10 µs. Zero with nothing recorded.
        /// </summary>
        public long Percentile(double fraction)
        {
            // Scanning from the longest durations down only has to get past the few presents allowed to take longer.
            int longer = (int)(count * (1 - fraction));
            int seen = 0;

            for (int i = bin_count - 1; i >= 0; i--)
            {
                seen += bins[i];

                if (seen > longer)
                    return (i + 1) * bin_ns;
            }

            return 0;
        }

        private static int binOf(long ns) => (int)Math.Clamp(ns / bin_ns, 0, bin_count - 1);
    }
}

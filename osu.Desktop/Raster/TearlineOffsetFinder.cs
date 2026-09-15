// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using osu.Framework.Logging;
using osu.Framework.Platform;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Finds the tear line offset from how long the compositor took to flip frames during previous plays.
    /// </summary>
    /// <remarks>
    /// A present timed for a scanline tears as far down the screen as scanout gets while the compositor flips it.
    /// Each play's swap-to-flip times are kept as a histogram. The offset centres the blanking interval on the span of flip times,
    /// as long as the blanking interval lasts, that holds the most flips across recent plays.
    /// </remarks>
    internal sealed class TearlineOffsetFinder
    {
        public sealed record Suggestion(int OffsetLines, double Coverage, double MedianLatencyMs, int Plays, long Flips);

        private const string filename = @"raster-sync-flips.json";

        private const long bin_ns = 10_000;
        private const int bin_count = 1000;

        private const int plays_kept = 20;
        private const long min_play_flips = 200;

        // Tearing flips follow the swap within a couple of milliseconds. A compositor holding frames for vblank takes most of a refresh.
        private const double max_median_latency_ms = 3;
        private const double max_latency_spread_ms = 1.5;

        private sealed class Play
        {
            public DateTimeOffset Date { get; set; }

            public string Display { get; set; } = string.Empty;

            public long Flips { get; set; }

            public long Misses { get; set; }

            /// <summary>
            /// Flips per 10 µs bin of swap-to-flip time. Flips past the last bin count towards <see cref="Flips"/> only.
            /// </summary>
            public Dictionary<int, long> Histogram { get; set; } = new Dictionary<int, long>();
        }

        private sealed class Recording
        {
            public List<Play> Plays { get; set; } = new List<Play>();
        }

        private sealed record Cached(string Display, int VDisplay, int VTotal, int Version, Suggestion? Value);

        private readonly Storage storage;
        private readonly object sync = new object();
        private readonly object saveSync = new object();
        private readonly Recording recording;

        private int version;
        private volatile Cached? cached;
        private volatile string? lastRejection;

        // The play being recorded, guarded by sync.
        private readonly long[] bins = new long[bin_count];
        private long flips;
        private long misses;
        private string? display;
        private bool playing;

        public TearlineOffsetFinder(Storage storage)
        {
            this.storage = storage;
            recording = load();
        }

        /// <summary>
        /// Update thread.
        /// </summary>
        public void BeginPlay()
        {
            lock (sync)
            {
                reset(null);
                playing = true;
            }
        }

        /// <summary>
        /// Keeps the play that just ended, if it recorded enough flips and they look like tearing. Update thread.
        /// </summary>
        public void EndPlay(bool waitForSave = false)
        {
            lock (sync)
            {
                if (!playing)
                    return;

                playing = false;

                if (display == null || flips < min_play_flips)
                    return;

                double median = percentileMs(0.5);
                double spread = percentileMs(0.75) - percentileMs(0.25);

                if (median > max_median_latency_ms)
                {
                    lastRejection = $"The last play was left out: its frames took {median:0.00} ms to reach the display, so the compositor was holding them for vblank instead of tearing.";
                    return;
                }

                if (spread > max_latency_spread_ms)
                {
                    lastRejection = $"The last play was left out: the compositor's flip times varied by {spread:0.00} ms.";
                    return;
                }

                var play = new Play
                {
                    Date = DateTimeOffset.Now,
                    Display = display,
                    Flips = flips,
                    Misses = misses,
                };

                for (int i = 0; i < bin_count; i++)
                {
                    if (bins[i] > 0)
                        play.Histogram[i] = bins[i];
                }

                recording.Plays.Add(play);

                var playsOfDisplay = recording.Plays.Where(p => p.Display == play.Display).ToList();
                foreach (var old in playsOfDisplay.Take(playsOfDisplay.Count - plays_kept))
                    recording.Plays.Remove(old);

                lastRejection = null;
                Interlocked.Increment(ref version);
            }

            save(waitForSave);
        }

        /// <summary>
        /// Probe thread.
        /// </summary>
        public void AddFlip(string flipDisplay, long latencyNs)
        {
            lock (sync)
            {
                if (!playing || latencyNs < 0)
                    return;

                // A play at another mode than it started in only counts from the change on.
                if (flipDisplay != display)
                    reset(flipDisplay);

                long bin = latencyNs / bin_ns;
                if (bin < bin_count)
                    bins[bin]++;

                flips++;
            }
        }

        /// <summary>
        /// Probe thread.
        /// </summary>
        public void AddMiss(string flipDisplay)
        {
            lock (sync)
            {
                if (playing && flipDisplay == display)
                    misses++;
            }
        }

        public void Forget()
        {
            lock (sync)
            {
                recording.Plays.Clear();
                lastRejection = null;
                Interlocked.Increment(ref version);
            }

            save(false);
        }

        /// <summary>
        /// The offset for a mode, or null if no play has been recorded at it. Cached, so cheap enough for the draw thread.
        /// </summary>
        public Suggestion? GetSuggestion(DrmVBlankClock.Timing timing)
        {
            var current = cached;

            if (current != null && current.Version == Volatile.Read(ref version) && current.Display == timing.Display
                && current.VDisplay == timing.VDisplay && current.VTotal == timing.VTotal)
                return current.Value;

            lock (sync)
            {
                var value = compute(timing);
                cached = new Cached(timing.Display, timing.VDisplay, timing.VTotal, version, value);
                return value;
            }
        }

        /// <summary>
        /// Update thread.
        /// </summary>
        public string Describe(DrmVBlankClock.Timing? timing)
        {
            string text;

            if (timing == null)
                text = @"Waiting for the display.";
            else if (GetSuggestion(timing) is Suggestion suggestion)
            {
                text = $"Found offset: {suggestion.OffsetLines} lines, from {suggestion.Flips} flips over {suggestion.Plays} plays. "
                       + $"The compositor takes {suggestion.MedianLatencyMs:0.00} ms to flip a frame (median), and {suggestion.Coverage:0%} of flips tear inside the blanking interval at this offset.";
            }
            else
                text = $"No plays recorded at {timing.Display} yet. Play with raster sync active to record flips.";

            if (lastRejection != null)
                text += $" {lastRejection}";

            lock (sync)
            {
                if (playing)
                    text += $" Recording this play: {flips} flips.";
            }

            return text;
        }

        private Suggestion? compute(DrmVBlankClock.Timing timing)
        {
            var plays = recording.Plays.Where(p => p.Display == timing.Display && p.Flips > 0).ToList();

            if (plays.Count == 0)
                return null;

            // Every play weighs the same, however long it was.
            double[] combined = new double[bin_count];

            foreach (var play in plays)
            {
                foreach (var (bin, count) in play.Histogram)
                {
                    if (bin >= 0 && bin < bin_count)
                        combined[bin] += (double)count / play.Flips / plays.Count;
                }
            }

            double lineNs = (double)timing.PeriodNs / timing.VTotal;
            int width = Math.Clamp((int)((timing.VTotal - timing.VDisplay) * lineNs / bin_ns), 1, bin_count);

            double sum = combined.Take(width).Sum();
            double best = sum;
            var bestStarts = new List<int> { 0 };

            for (int start = 1; start + width <= bin_count; start++)
            {
                sum += combined[start + width - 1] - combined[start - 1];

                if (sum > best + 1e-9)
                {
                    best = sum;
                    bestStarts.Clear();
                    bestStarts.Add(start);
                }
                else if (sum > best - 1e-9)
                    bestStarts.Add(start);
            }

            // Centring on the flips inside the span, rather than on the span, keeps the offset from snapping to bins,
            // and leaves room either side when the flips are narrower than the blanking interval.
            int bestStart = bestStarts[bestStarts.Count / 2];
            double insideFlips = 0;
            double insideNs = 0;

            for (int i = bestStart; i < bestStart + width; i++)
            {
                insideFlips += combined[i];
                insideNs += combined[i] * (i + 0.5) * bin_ns;
            }

            double centreNs = insideFlips > 0 ? insideNs / insideFlips : (bestStart + width / 2.0) * bin_ns;

            int windowStart = (int)Math.Round(centreNs / bin_ns - width / 2.0);
            double coverage = 0;

            for (int i = Math.Max(0, windowStart); i < Math.Min(bin_count, windowStart + width); i++)
                coverage += combined[i];

            double medianNs = bin_count * bin_ns;
            double seen = 0;

            for (int i = 0; i < bin_count; i++)
            {
                seen += combined[i];

                if (seen >= 0.5)
                {
                    medianNs = (i + 0.5) * bin_ns;
                    break;
                }
            }

            return new Suggestion(-(int)Math.Round(centreNs / lineNs), coverage, medianNs / 1e6, plays.Count, plays.Sum(p => p.Flips));
        }

        private double percentileMs(double fraction)
        {
            long target = (long)Math.Ceiling(flips * fraction);
            long seen = 0;

            for (int i = 0; i < bin_count; i++)
            {
                seen += bins[i];

                if (seen >= target)
                    return (i + 0.5) * bin_ns / 1e6;
            }

            return bin_count * bin_ns / 1e6;
        }

        private void reset(string? newDisplay)
        {
            Array.Clear(bins);
            flips = 0;
            misses = 0;
            display = newDisplay;
        }

        private Recording load()
        {
            try
            {
                if (storage.Exists(filename))
                {
                    using (var stream = storage.GetStream(filename))
                    using (var reader = new StreamReader(stream))
                        return JsonConvert.DeserializeObject<Recording>(reader.ReadToEnd()) ?? new Recording();
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, @"Raster sync could not read its recorded flips");
            }

            return new Recording();
        }

        private void save(bool wait)
        {
            string json;

            lock (sync)
                json = JsonConvert.SerializeObject(recording);

            if (wait)
                write(json);
            else
                Task.Run(() => write(json));
        }

        private void write(string json)
        {
            lock (saveSync)
            {
                try
                {
                    using (var stream = storage.GetStream(filename, FileAccess.Write, FileMode.Create))
                    using (var writer = new StreamWriter(stream))
                        writer.Write(json);
                }
                catch (Exception e)
                {
                    Logger.Error(e, @"Raster sync could not save its recorded flips");
                }
            }
        }
    }
}

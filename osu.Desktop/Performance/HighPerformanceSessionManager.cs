// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime;
using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Logging;
using osu.Game.Performance;

namespace osu.Desktop.Performance
{
    public class HighPerformanceSessionManager : IHighPerformanceSessionManager
    {
        public bool IsSessionActive => activeSessions > 0;

        /// <summary>
        /// The mode gameplay runs the collector in.
        /// </summary>
        /// <remarks>
        /// <see cref="GCLatencyMode.LowLatency"/>, which gameplay used to ask for, holds the gen0 budget at 256 KiB
        /// however large it is asked to be: measured in a play, 15.7 collections a second pausing 0.61 ms each.
        /// Leaving the budget alone lets gen0 reach 16 MB and collect a fifth of a time a second instead, which measured
        /// fourteen times less pause time, 0.19 ms off the margin a frame starts on, and a whole extra frame slice per
        /// refresh, with nothing finishing late. The rare collection that remains is a long one, so this is still worth
        /// being able to change for a play.
        /// </remarks>
        private static readonly GCLatencyMode gameplay_gc_mode = readGameplayMode();

        private static GCLatencyMode readGameplayMode()
        {
            string? requested = Environment.GetEnvironmentVariable(@"OSU_GAMEPLAY_GC_MODE");

            // NoGCRegion cannot be entered by assigning it, and Batch turns off concurrency for the whole process.
            if (!Enum.TryParse(requested, true, out GCLatencyMode mode)
                || (mode != GCLatencyMode.Interactive && mode != GCLatencyMode.LowLatency && mode != GCLatencyMode.SustainedLowLatency))
            {
                return GCLatencyMode.Interactive;
            }

            return mode;
        }

        private int activeSessions;

        private GCLatencyMode originalGCMode;

        public IDisposable BeginSession()
        {
            enterSession();
            return new InvokeOnDisposal<HighPerformanceSessionManager>(this, static m => m.exitSession());
        }

        private void enterSession()
        {
            if (Interlocked.Increment(ref activeSessions) > 1)
            {
                Logger.Log($"High performance session requested ({activeSessions} running in total)");
                return;
            }

            Logger.Log($"Starting high performance session (GC latency mode {gameplay_gc_mode})");

            originalGCMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = gameplay_gc_mode;

            // Without doing this, the new GC mode won't kick in until the next GC, which could be at a more noticeable point in time.
            GC.Collect(0);
        }

        private void exitSession()
        {
            if (Interlocked.Decrement(ref activeSessions) > 0)
            {
                Logger.Log($"High performance session finished ({activeSessions} others remain)");
                return;
            }

            Logger.Log("Ending high performance session");

            if (GCSettings.LatencyMode == gameplay_gc_mode)
                GCSettings.LatencyMode = originalGCMode;

            // No GC.Collect() as we were already collecting at a higher frequency in the old mode.
        }
    }
}

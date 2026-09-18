// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Timing;

namespace osu.Game.Beatmaps
{
    /// <summary>
    /// Runs a clock ahead of its source by a lead that changes every frame, without ever stepping back unless the source does.
    /// </summary>
    /// <remarks>
    /// The lead holds the clock still across the update frames that one present will show. A still clock sitting on a source that drifts by
    /// microseconds would step back now and then, and a clock that has stepped back counts as rewinding until it next moves, which drops the
    /// player's input for every still frame after: measured in play, taps on time were ignored and notes missed on their own. So the lead only
    /// ever holds time or moves it forward, while seeks and rewinds of the source itself pass through.
    /// </remarks>
    public sealed class ScanoutTimedClock : IFrameBasedClock
    {
        /// <summary>
        /// How far ahead of the source to run, in milliseconds of real time.
        /// </summary>
        public double Lead { get; set; }

        private readonly IFrameBasedClock source;

        private double lastSourceTime = double.NaN;

        public ScanoutTimedClock(IFrameBasedClock source)
        {
            this.source = source;
        }

        public double CurrentTime { get; private set; }

        public double ElapsedFrameTime { get; private set; }

        public double Rate => source.Rate;

        public bool IsRunning => source.IsRunning;

        public double FramesPerSecond => source.FramesPerSecond;

        public void ProcessFrame()
        {
            source.ProcessFrame();

            double sourceTime = source.CurrentTime;
            double proposed = sourceTime + Lead * source.Rate;
            double previous = CurrentTime;

            if (!double.IsNaN(lastSourceTime) && sourceTime >= lastSourceTime && proposed < previous)
                proposed = previous;

            ElapsedFrameTime = double.IsNaN(lastSourceTime) ? 0 : proposed - previous;
            CurrentTime = proposed;
            lastSourceTime = sourceTime;
        }
    }
}

// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Game.Graphics.Raster
{
    /// <summary>
    /// Paces presented frames against the display's scanout. Provided by hosts that can see the display's vertical blanking.
    /// </summary>
    public interface IRasterSync
    {
        /// <summary>
        /// The number of frames presented so far. Safe to read from the draw thread.
        /// </summary>
        long PresentCount { get; }

        /// <summary>
        /// Which slice of the refresh the frame being drawn is timed for, counting down from the top of the screen,
        /// or null unless the frame is timed for one of several slices. Draw thread only.
        /// </summary>
        int? PlannedSlice { get; }

        /// <summary>
        /// What raster sync is currently doing, or why it is not.
        /// </summary>
        string Status { get; }

        /// <summary>
        /// Where the tear line is being steered to, and from which recorded flips.
        /// </summary>
        string TearlineSteeringStatus { get; }

        /// <summary>
        /// Discards the flips recorded in previous plays.
        /// </summary>
        void ForgetRecordedFlips();
    }
}

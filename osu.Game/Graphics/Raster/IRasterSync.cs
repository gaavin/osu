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
        /// What raster sync is currently doing, or why it is not.
        /// </summary>
        string Status { get; }

        /// <summary>
        /// What the tear line offset finder has recorded, and the offset it found.
        /// </summary>
        string OffsetFinderStatus { get; }

        /// <summary>
        /// The tear line offset found from flips recorded in previous plays at the current display mode, or null without any.
        /// </summary>
        int? FoundTearlineOffset { get; }

        /// <summary>
        /// Discards the flips recorded in previous plays.
        /// </summary>
        void ForgetRecordedFlips();
    }
}

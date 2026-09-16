// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osuTK;

namespace osu.Game.Graphics.Raster
{
    /// <summary>
    /// Lets a cursor be drawn where the pen is when the frame is drawn, rather than where it was when the update frame that built the frame read input.
    /// Provided by hosts that see the tablet's reports as they arrive.
    /// </summary>
    public interface IPointerLatch
    {
        /// <summary>
        /// Whether a cursor at this screen space position is following the pen, which is when it sits on one of the pen's recent reports.
        /// A cursor that follows a replay, a mouse or nothing at all is left where the update frame put it. Update thread.
        /// </summary>
        bool IsFollowingPen(Vector2 screenSpacePosition);

        /// <summary>
        /// How far the pen has moved from a position <see cref="IsFollowingPen"/> accepted, in screen space, as of now. Draw thread.
        /// </summary>
        Vector2 TakeOffset(Vector2 screenSpacePosition);
    }
}

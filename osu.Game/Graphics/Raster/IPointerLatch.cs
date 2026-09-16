// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Rendering;
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

        /// <summary>
        /// Whether cursor draws are being held this frame until the rest of it has finished on the GPU. Draw thread.
        /// </summary>
        bool DefersDraws { get; }

        /// <summary>
        /// Hands a cursor's draw over to be done once the rest of the frame has finished on the GPU, just before it is presented,
        /// where it takes the newest pen report as of then. Returns false if draws are not held this frame, or one already is,
        /// in which case the caller draws as usual. Draw thread.
        /// </summary>
        bool TryDeferDraw(ILatchedDraw draw);
    }

    /// <summary>
    /// A draw that can be held until just before the frame is presented.
    /// </summary>
    public interface ILatchedDraw
    {
        /// <summary>
        /// Draws on top of the finished frame. Draw thread, before the buffer holding the draw's state is released.
        /// </summary>
        void DrawLatched(IRenderer renderer);
    }
}

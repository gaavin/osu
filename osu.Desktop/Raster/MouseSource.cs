// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Logging;
using osu.Framework.Platform;
using Vector2 = osuTK.Vector2;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Sees each of the mouse's reports as SDL hands it over, on its way to the mouse handler, so <see cref="PointerLatch"/> can draw the cursor at the newest one.
    /// </summary>
    /// <remarks>
    /// The window's mouse move event is where the mouse handler reads the positions it queues, and an event calls its subscribers in the
    /// order they subscribed, so the source subscribes as the window is made, before the input handlers are initialised. The position is
    /// the one the handler goes on to queue, in the same screen space the update frame places the cursor in.
    ///
    /// The window type is internal to the framework, so the event is subscribed to by reflection. Reports arrive as the window thread polls
    /// SDL rather than as the mouse sends them, which is the tablet's advantage: what the draw thread can still correct is the reports that
    /// were polled after the update frame read its input.
    ///
    /// In relative mouse mode the window reports movement rather than positions, and the cursor is left where update frames put it.
    /// <see cref="RasterSyncLinuxGameHost"/> keeps relative mode off.
    /// </remarks>
    internal sealed class MouseSource : PointerSource
    {
        private MouseSource()
            : base(@"mouse", timed: false)
        {
        }

        /// <summary>
        /// Subscribes a source to the window's mouse move event, as the window is made. Returns null if the window does not have one.
        /// </summary>
        public static MouseSource? TryFollow(IWindow window, PointerLatch latch)
        {
            var mouseMove = window.GetType().GetEvent(@"MouseMove");

            if (mouseMove?.EventHandlerType != typeof(Action<Vector2>))
            {
                Logger.Log(@"Pointer latch: the window does not report mouse moves as expected, so the cursor is drawn where update frames put it.");
                return null;
            }

            var source = new MouseSource();

            mouseMove.AddEventHandler(window, new Action<Vector2>(source.handleMouseMove));
            latch.Add(source);
            return source;
        }

        private void handleMouseMove(Vector2 position) => Report(position.X, position.Y);
    }
}

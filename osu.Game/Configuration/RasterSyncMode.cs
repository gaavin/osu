// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.ComponentModel;

namespace osu.Game.Configuration
{
    public enum RasterSyncMode
    {
        [Description("Off")]
        Disabled,

        /// <summary>
        /// One present per refresh, timed so the tear line falls in the blanking interval.
        /// </summary>
        [Description("Lagless VSync (tear line hidden in blanking)")]
        TearlineSync,

        /// <summary>
        /// Several presents per refresh at evenly spaced scanlines, each drawn just before scanout reaches its slice.
        /// </summary>
        [Description("Frame slices (beam racing)")]
        FrameSlices,

        /// <summary>
        /// As <see cref="TearlineSync"/>, plus a second present each refresh whose tear line sits just above the cursor, drawing the cursor
        /// last at the newest pen report, so the cursor is scanned out right after it is drawn.
        /// </summary>
        [Description("Cursor chasing (tear line above the cursor)")]
        CursorChasing,
    }
}

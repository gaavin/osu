// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Platform.Pointer;
using osu.Framework.Bindables;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.Handlers.Tablet;
using osu.Framework.Logging;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// Sees each of the tablet's reports as it arrives, on its way to the tablet handler, so <see cref="PointerLatch"/> can draw the cursor at the newest one.
    /// </summary>
    /// <remarks>
    /// It sits between OpenTabletDriver's output mode and the handler rather than replacing the handler, because the input settings are
    /// saved under the handler's type and a subclass would lose the tablet area.
    /// </remarks>
    internal sealed class PenSource : PointerSource, IAbsolutePointer, IPressureHandler
    {
        private static readonly FieldInfo? output_mode_field = typeof(OpenTabletDriverHandler).GetField(@"outputMode", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly PointerLatch latch;
        private readonly OpenTabletDriverHandler handler;
        private readonly IAbsolutePointer pointer;
        private readonly IPressureHandler pressure;
        private readonly IBindable<TabletInfo?> tablet;

        private PenSource(PointerLatch latch, OpenTabletDriverHandler handler)
            : base(@"pen")
        {
            this.latch = latch;
            this.handler = handler;
            pointer = handler;
            pressure = handler;

            // The handler makes a new output mode each time a tablet is detected, which happens on OpenTabletDriver's own thread
            // well after the handlers are initialised, and again whenever the tablet is reconnected. The tablet it reports is set
            // just after, so that is when to stand in front of the new one.
            tablet = handler.Tablet.GetBoundCopy();
            tablet.BindValueChanged(t =>
            {
                if (t.NewValue != null)
                    attach(t.NewValue);
            }, true);
        }

        private void attach(TabletInfo info)
        {
            if (output_mode_field?.GetValue(handler) is not AbsoluteOutputMode outputMode)
            {
                Logger.Log($@"Pointer latch: {info.Name} has no absolute output mode, so the cursor is drawn where update frames put it.");
                return;
            }

            if (outputMode.Pointer == this)
                return;

            if (outputMode.Pointer != handler)
            {
                Logger.Log($@"Pointer latch: {info.Name} reports to something other than the tablet handler, so the cursor is drawn where update frames put it.");
                return;
            }

            outputMode.Pointer = this;
            Logger.Log($@"Pointer latch: the pen follows {info.Name}.");
            latch.Add(this);
        }

        /// <summary>
        /// Puts a source between OpenTabletDriver and the tablet handler, once the handlers have been initialised. Returns null if it cannot.
        /// </summary>
        public static PenSource? TryFollow(IEnumerable<InputHandler> handlers, PointerLatch latch)
        {
            var handler = handlers.OfType<OpenTabletDriverHandler>().SingleOrDefault();

            if (handler == null)
                return null;

            // OpenTabletDriver looks for these on the pointer it reports to. Standing in for a handler that implements more
            // than the source forwards would silently drop them, so a framework that adds one leaves the source out.
            var pointerInterfaces = handler.GetType().GetInterfaces().Where(i => i.Namespace == typeof(IAbsolutePointer).Namespace).ToArray();
            var forwarded = new[] { typeof(IAbsolutePointer), typeof(IRelativePointer), typeof(IPressureHandler) };

            if (pointerInterfaces.Except(forwarded).Any() || output_mode_field?.FieldType != typeof(AbsoluteOutputMode))
            {
                Logger.Log(@"Pointer latch: the tablet handler is not laid out as expected, so the cursor is drawn where update frames put it.");
                return null;
            }

            return new PenSource(latch, handler);
        }

        void IAbsolutePointer.SetPosition(System.Numerics.Vector2 pos)
        {
            Report(pos.X, pos.Y);
            pointer.SetPosition(pos);
        }

        void IPressureHandler.SetPressure(float percentage) => pressure.SetPressure(percentage);
    }
}

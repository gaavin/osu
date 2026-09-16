// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using osu.Framework;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Input;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.Handlers.Mouse;
using osu.Framework.Platform;
using osu.Framework.Platform.Linux;
using osuTK.Graphics.ES30;

namespace osu.Desktop.Raster
{
    /// <summary>
    /// <see cref="LinuxGameHost"/>, with presents timed against the display's scanout by <see cref="RasterSyncController"/>.
    /// </summary>
    internal class RasterSyncLinuxGameHost : DesktopGameHost
    {
        /// <summary>
        /// How long to wait for the GPU to finish a frame before presenting it regardless. Long enough that only a wedged GPU reaches it.
        /// </summary>
        private const long gpu_wait_timeout_ns = 1_000_000_000;

        public readonly RasterSyncController RasterSync;

        private static readonly MethodInfo? update_frame_sync_mode = typeof(GameHost).GetMethod("updateFrameSyncMode", BindingFlags.Instance | BindingFlags.NonPublic);

        private Bindable<WindowMode> windowMode = null!;
        private Bindable<FrameSync> frameSync = null!;
        private Bindable<ExecutionMode> executionMode = null!;

        public RasterSyncLinuxGameHost(string gameName, HostOptions? options)
            : base(gameName, options)
        {
            RasterSync = new RasterSyncController(this);
        }

        // LinuxGameHost's constructor is internal, so what it overrides is repeated here. Its SDL windows are internal too.
        protected override IWindow CreateWindow(GraphicsSurfaceType preferredSurface)
        {
            string windowType = FrameworkEnvironment.UseSDL3 ? @"osu.Framework.Platform.Linux.SDL3LinuxWindow" : @"osu.Framework.Platform.Linux.SDL2LinuxWindow";
            return (IWindow)Activator.CreateInstance(typeof(LinuxGameHost).Assembly.GetType(windowType, true)!, preferredSurface, Options.FriendlyGameName, Options.BypassCompositor)!;
        }

        protected override ReadableKeyCombinationProvider CreateReadableKeyCombinationProvider() => new LinuxReadableKeyCombinationProvider();

        protected override IEnumerable<InputHandler> CreateAvailableInputHandlers()
        {
            var handlers = base.CreateAvailableInputHandlers();

            foreach (var h in handlers.OfType<MouseHandler>())
            {
                // There are several bugs we need to fix with Linux / SDL3 cursor handling before switching this on.
                h.UseRelativeMode.Value = false;
                h.UseRelativeMode.Default = false;
            }

            return handlers;
        }

        protected override void SetupConfig(IDictionary<FrameworkSetting, object> defaultOverrides)
        {
            base.SetupConfig(defaultOverrides);

            windowMode = Config.GetBindable<WindowMode>(FrameworkSetting.WindowMode);
            frameSync = Config.GetBindable<FrameSync>(FrameworkSetting.FrameSync);
            executionMode = Config.GetBindable<ExecutionMode>(FrameworkSetting.ExecutionMode);
        }

        /// <summary>
        /// Lifts the 1000 Hz cap on update and draw frames that applies with the frame limiter on Unlimited.
        /// The draw thread can then present several slices per refresh, and the scene it draws is never older than one short update frame.
        /// Update thread.
        /// </summary>
        public void SetUnlimitedFrames(bool unlimited)
        {
            if (AllowBenchmarkUnlimitedFrames == unlimited)
                return;

            AllowBenchmarkUnlimitedFrames = unlimited;

            // The limits are otherwise only recalculated when the frame limiter or the display mode changes.
            update_frame_sync_mode?.Invoke(this, null);
        }

        protected override void UpdateFrame()
        {
            base.UpdateFrame();

#if !RASTER_METRICS
            if (UpdateSync.MODE == UpdateSync.SyncMode.Off)
                return;
#endif

            // The scene is published by now. Waiting here rather than before the frame means the clock, which the update thread
            // processes after this returns, and the input collected in the next frame are both read once the wait is over.
            RasterSync.UpdateSync.FinishUpdateFrame(executionMode.Value != ExecutionMode.SingleThread);
        }

        protected override void DrawFrame()
        {
            if (!RasterSync.ShouldPace(findBlocker()))
            {
                base.DrawFrame();
                return;
            }

            if (executionMode.Value == ExecutionMode.SingleThread)
            {
                // Input, audio and update frames run on this thread right after this draw frame, so the wait for the next present goes last.
                base.DrawFrame();
                RasterSync.PlanNextPresent();
            }
            else
            {
                RasterSync.PlanNextPresent();
                base.DrawFrame();
            }
        }

        private string? findBlocker()
        {
            if (Window == null || !IsActive.Value)
                return @"Waiting for the game window to be focused";

            // Only a fullscreen surface is flipped to the display directly, without being composited at vblank.
            if (windowMode.Value != WindowMode.Fullscreen && windowMode.Value != WindowMode.Borderless)
                return @"Needs fullscreen or borderless";

            if (frameSync.Value == FrameSync.VSync)
                return @"Needs a frame limiter other than VSync";

            if (ResolvedRenderer != RendererType.OpenGL)
                return @"Needs the OpenGL renderer";

            return null;
        }

        protected override void Swap()
        {
            if (!RasterSync.HasPlannedPresent)
            {
                base.Swap();
                RasterSync.CountPresent();
                return;
            }

            // The compositor waits for the GPU to finish a buffer before flipping it, so the wait for the scanline starts once it has.
            // A fence covers this frame's commands alone, where glFinish waits for everything the context still has outstanding.
            IntPtr fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);

#if RASTER_METRICS
            RasterSync.NoteDrawFinished();
#endif

            GL.ClientWaitSync(fence, ClientWaitSyncFlags.SyncFlushCommandsBit, gpu_wait_timeout_ns);
            GL.DeleteSync(fence);

            RasterSync.WaitForPlannedPresent();
            base.Swap();
            RasterSync.CompletePresent();
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            RasterSync.Dispose();
        }
    }
}

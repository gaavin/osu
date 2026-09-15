// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Graphics.Raster;
using osu.Game.Graphics.UserInterfaceV2;

namespace osu.Game.Overlays.Settings.Sections.Graphics
{
    public partial class RasterSyncSettings : SettingsSubsection
    {
        protected override LocalisableString Header => @"Raster sync";

        [Resolved(canBeNull: true)]
        private IRasterSync? rasterSync { get; set; }

        private readonly Bindable<SettingsNote.Data?> statusNote = new Bindable<SettingsNote.Data?>();
        private readonly Bindable<SettingsNote.Data?> steeringNote = new Bindable<SettingsNote.Data?>();
        private readonly BindableBool slicesCanBeShown = new BindableBool();

        private string? lastStatus;
        private string? lastSteeringStatus;

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            var mode = config.GetBindable<RasterSyncMode>(OsuSetting.RasterSyncMode);

            Children = new Drawable[]
            {
                new SettingsItemV2(new FormEnumDropdown<RasterSyncMode>
                {
                    Caption = @"Raster sync",
                    HintText = @"Times each frame against the display's scanout during gameplay. Menus draw as usual. Needs fullscreen, the OpenGL renderer and a frame limiter other than VSync.",
                    Current = mode,
                })
                {
                    Note = { BindTarget = statusNote },
                    Keywords = new[] { @"beam racing", @"lagless", @"vsync", @"tearing", @"scanline", @"latency" },
                },
                new SettingsItemV2(new FormSliderBar<int>
                {
                    Caption = @"Frame slices per refresh",
                    Current = config.GetBindable<int>(OsuSetting.RasterFrameSlices),
                    KeyboardStep = 1,
                })
                {
                    CanBeShown = { BindTarget = slicesCanBeShown },
                },
                new SettingsItemV2(new FormSliderBar<double>
                {
                    Caption = @"Render headroom",
                    HintText = @"Time kept spare on top of the longest recent frame. Less is lower latency, until frames start finishing late and the tear line jumps.",
                    Current = config.GetBindable<double>(OsuSetting.RasterRenderHeadroom),
                    KeyboardStep = 0.05f,
                    LabelFormat = v => $@"{v:0.00} ms",
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = @"Show tear line indicator",
                    HintText = @"The tear line is steered into the blanking interval during plays, from how long the compositor takes to flip frames.",
                    Current = config.GetBindable<bool>(OsuSetting.RasterShowTearline),
                })
                {
                    Note = { BindTarget = steeringNote },
                    Keywords = new[] { @"tear line offset", @"calibrate" },
                },
                new DangerousSettingsButtonV2
                {
                    Text = @"Forget recorded flips",
                    Action = () => rasterSync?.ForgetRecordedFlips(),
                },
            };

            mode.BindValueChanged(m => slicesCanBeShown.Value = m.NewValue == RasterSyncMode.FrameSlices, true);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            updateStatus();
            Scheduler.AddDelayed(updateStatus, 500, true);
        }

        private void updateStatus()
        {
            string status = rasterSync?.Status ?? @"Only available on Linux.";
            string steeringStatus = rasterSync?.TearlineSteeringStatus ?? @"Only available on Linux.";

            if (status != lastStatus)
            {
                lastStatus = status;
                statusNote.Value = new SettingsNote.Data(status, SettingsNote.Type.Informational);
            }

            if (steeringStatus != lastSteeringStatus)
            {
                lastSteeringStatus = steeringStatus;
                steeringNote.Value = new SettingsNote.Data(steeringStatus, SettingsNote.Type.Informational);
            }
        }
    }
}

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
        private readonly Bindable<SettingsNote.Data?> finderNote = new Bindable<SettingsNote.Data?>();
        private readonly BindableBool slicesCanBeShown = new BindableBool();

        private Bindable<int> offset = null!;
        private Bindable<bool> autoOffset = null!;
        private SettingsButtonV2 useFoundOffsetButton = null!;

        private string? lastStatus;
        private string? lastFinderStatus;

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            var mode = config.GetBindable<RasterSyncMode>(OsuSetting.RasterSyncMode);
            offset = config.GetBindable<int>(OsuSetting.RasterTearlineOffset);
            autoOffset = config.GetBindable<bool>(OsuSetting.RasterAutoTearlineOffset);

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
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = @"Find tear line offset from previous plays",
                    HintText = @"During plays, times how long the compositor takes to flip frames, and aims the tear line so the most flips land in the blanking interval.",
                    Current = autoOffset,
                })
                {
                    Note = { BindTarget = finderNote },
                    Keywords = new[] { @"calibrate", @"automatic" },
                },
                new SettingsItemV2(new FormSliderBar<int>
                {
                    Caption = @"Tear line offset",
                    HintText = @"Scanlines to move the tear line by, on top of the found offset when that is on. Negative moves it up. "
                               + @"To set it by eye, turn on the indicator, find where the tear line enters the bottom and the top of the screen, then settle halfway.",
                    Current = offset,
                    KeyboardStep = 1,
                    LabelFormat = v => $@"{v} lines",
                }),
                useFoundOffsetButton = new SettingsButtonV2
                {
                    Text = @"Use the found offset as the manual offset",
                    Action = () =>
                    {
                        if (rasterSync?.FoundTearlineOffset is not int found)
                            return;

                        offset.Value = found + (autoOffset.Value ? offset.Value : 0);
                        autoOffset.Value = false;
                    },
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
                    Current = config.GetBindable<bool>(OsuSetting.RasterShowTearline),
                }),
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
            string finderStatus = rasterSync?.OffsetFinderStatus ?? @"Only available on Linux.";

            if (status != lastStatus)
            {
                lastStatus = status;
                statusNote.Value = new SettingsNote.Data(status, SettingsNote.Type.Informational);
            }

            if (finderStatus != lastFinderStatus)
            {
                lastFinderStatus = finderStatus;
                finderNote.Value = new SettingsNote.Data(finderStatus, SettingsNote.Type.Informational);
            }

            useFoundOffsetButton.Enabled.Value = rasterSync?.FoundTearlineOffset != null;
        }
    }
}

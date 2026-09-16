// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Game.Configuration;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Graphics.Raster
{
    /// <summary>
    /// A strip down the left edge of the screen that shows where frames take over from each other.
    /// </summary>
    /// <remarks>
    /// Frames timed for slices of a refresh are coloured by slice, so the strip holds still with any number of slices and a slice left without a frame
    /// shows as a band two slices tall. Other frames flip the colour with every present: wherever a tear line crosses the strip, it splits into two colours,
    /// and with the tear line hidden in blanking, it flickers evenly.
    /// </remarks>
    public partial class TearlineIndicator : Drawable
    {
        private const float strip_width = 48;
        private const float tick_width = 16;
        private const float tick_height = 2;

        [Resolved(canBeNull: true)]
        private IRasterSync? rasterSync { get; set; }

        private IShader shader = null!;
        private Bindable<bool> show = null!;

        public TearlineIndicator()
        {
            Anchor = Anchor.TopLeft;
            Origin = Anchor.TopLeft;
            RelativeSizeAxes = Axes.Y;
            Width = strip_width + tick_width;
        }

        [BackgroundDependencyLoader]
        private void load(ShaderManager shaders, OsuConfigManager config)
        {
            shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
            show = config.GetBindable<bool>(OsuSetting.RasterShowTearline);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            show.BindValueChanged(s => Alpha = s.NewValue ? 1 : 0, true);
        }

        protected override DrawNode CreateDrawNode() => new TearlineIndicatorDrawNode(this);

        private class TearlineIndicatorDrawNode : DrawNode
        {
            protected new TearlineIndicator Source => (TearlineIndicator)base.Source;

            private IShader shader = null!;
            private IRasterSync? rasterSync;
            private Vector2 drawSize;

            // Draw nodes take turns across frames, so a count kept per node would not advance once per frame.
            private static long drawCount;

            public TearlineIndicatorDrawNode(TearlineIndicator source)
                : base(source)
            {
            }

            public override void ApplyState()
            {
                base.ApplyState();

                shader = Source.shader;
                rasterSync = Source.rasterSync;
                drawSize = Source.DrawSize;
            }

            protected override void Draw(IRenderer renderer)
            {
                base.Draw(renderer);

                long frame = rasterSync?.PlannedSlice ?? rasterSync?.PresentCount ?? drawCount++;

                shader.Bind();

                drawRectangle(renderer, Vector2.Zero, new Vector2(strip_width, drawSize.Y), frame % 2 == 0 ? Color4.Magenta : Color4.Lime);

                // Quarter marks, to tell where along the screen a tear line sits.
                for (int i = 1; i < 4; i++)
                    drawRectangle(renderer, new Vector2(strip_width, drawSize.Y * i / 4 - tick_height / 2), new Vector2(tick_width, tick_height), Color4.White);

                shader.Unbind();
            }

            private void drawRectangle(IRenderer renderer, Vector2 topLeft, Vector2 size, Color4 colour)
            {
                renderer.DrawQuad(
                    renderer.WhitePixel,
                    new Quad(
                        Vector2Extensions.Transform(topLeft, DrawInfo.Matrix),
                        Vector2Extensions.Transform(topLeft + new Vector2(size.X, 0), DrawInfo.Matrix),
                        Vector2Extensions.Transform(topLeft + new Vector2(0, size.Y), DrawInfo.Matrix),
                        Vector2Extensions.Transform(topLeft + size, DrawInfo.Matrix)
                    ),
                    colour);
            }
        }
    }
}

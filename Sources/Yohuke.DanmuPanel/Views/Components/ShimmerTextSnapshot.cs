using Avalonia;
using Avalonia.Media;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class ShimmerTextSnapshot : IDisposable
{
    private readonly List<(IImmutableGlyphRunReference GlyphRun, IImmutableBrush Foreground, Matrix Transform)> glyphs = [];

    internal Rect Bounds { get; }

    internal ShimmerTextSnapshot(DrawingGroup drawing, Size size)
    {
        Bounds = new(size);
        Collect(drawing, Matrix.Identity);
    }

    internal void Draw(ImmediateDrawingContext context)
    {
        foreach (var glyph in glyphs)
        {
            using (context.PushPreTransform(glyph.Transform))
            {
                context.DrawGlyphRun(glyph.Foreground, glyph.GlyphRun);
            }
        }
    }

    public void Dispose()
    {
        foreach (var glyph in glyphs)
        {
            glyph.GlyphRun.Dispose();
        }
    }

    private void Collect(Drawing drawing, Matrix transform)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                var childTransform = (group.Transform?.Value ?? Matrix.Identity) * transform;
                foreach (var child in group.Children)
                {
                    Collect(child, childTransform);
                }
                break;
            case GlyphRunDrawing { GlyphRun: { } run, Foreground: { } foreground }:
                if (run.TryCreateImmutableGlyphRunReference() is { } reference)
                {
                    glyphs.Add((reference, foreground.ToImmutable(), transform));
                }
                break;
        }
    }
}

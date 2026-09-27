using FlowMonitor.Model;
using FlowMonitor.Rendering;
using Vortice.Direct2D1;
using System.Numerics;
using Color4 = Vortice.Mathematics.Color4;
using RectF = System.Drawing.RectangleF;
using V2 = System.Numerics.Vector2;

namespace FlowMonitor.Widgets;

/// <summary>Chrome around the chart: card background, edit-mode badges, resize affordance.</summary>
public static class WidgetPainter
{
    static readonly Color4 CardBase = new(0.055f, 0.059f, 0.070f, 1f);
    static readonly Color4 BorderIdle = new(1f, 1f, 1f, 0.07f);
    static readonly Color4 BorderHover = new(1f, 1f, 1f, 0.17f);
    static readonly Color4 ArrowInk = new(1f, 1f, 1f, 0.60f);

    /// <summary>Badge glyph half-extent as a fraction of its box, at full ease.</summary>
    public const float BadgeInk = 0.32f;
    /// <summary>Drawn half-width of a badge glyph in logical px (HeaderLayout reserves this).</summary>
    public static readonly float BadgeGlyphHalf = WidgetWindow.CheckMarkSize * BadgeInk;
    /// <summary>Drawn half-width of a metric chevron in logical px at full ease.</summary>
    public const float ChevronGlyphHalf = 6f;
    /// <summary>Corner bracket inset from the card edge, in logical px.</summary>
    public const float CornerInset = 5.5f;
    /// <summary>Corner bracket arm length, in logical px.</summary>
    public const float CornerLen = 11f;
    /// <summary>Outermost strip a corner bracket occupies (HeaderLayout keeps badges clear of it).</summary>
    public const float CornerSpan = CornerInset + CornerLen;

    public static void PaintBackground(ID2D1DeviceContext dc, ResourceCache res, WidgetConfig cfg,
        float width, float height, float hover, float checkAmount, bool editing)
    {
        float s = cfg.Dpi;
        float radius = cfg.CornerRadius * s;

        // Transparency 0 = fully opaque card, 100 = invisible.
        float baseAlpha = 1f - Math.Clamp(cfg.Transparency / 100f, 0f, 1f);
        float alpha = Math.Clamp(baseAlpha + (editing ? 0.05f * checkAmount : 0.012f * hover), 0.02f, 1f);

        // No outline: borderless card, hover feedback via fill alone.
        var rect = new RoundedRectangle(new RectF(0f, 0f, width, height), radius, radius);
        dc.FillRoundedRectangle(rect, res.Brush(new Color4(CardBase.R, CardBase.G, CardBase.B, alpha)));
    }

    /// <summary>
    /// Confirm badge: glyph only, no disc. Hover brightens the glyph; edit fade via amount.
    /// </summary>
    public static void PaintCheckMark(ID2D1DeviceContext dc, ResourceCache res, WidgetConfig cfg,
        float width, float height, float amount, float hover = 0f)
    {
        if (amount <= 0.01f) return;
        float s = cfg.Dpi;
        float ease = Ease(amount);
        // Placement from WidgetWindow.CheckBadgeRect, shared with the hit test.
        // Shrink anchored to rect centre so ease-in does not walk the badge.
        int logicalW = (int)MathF.Round(width / s);
        RectF lr = WidgetWindow.CheckBadgeRect(logicalW);
        float size = WidgetWindow.CheckMarkSize * s * (0.70f + 0.30f * ease);
        var center = new V2((lr.Left + lr.Width * 0.5f) * s, (lr.Top + lr.Height * 0.5f) * s);
        float sc = 0.5f + 0.5f * ease;

        dc.PushAxisAlignedClip(new RectF(0, 0, width, height), AntialiasMode.Aliased);
        dc.Transform = Matrix3x2.CreateScale(sc, sc, center);

        // ✓ glyph, sized as before; only its brightness reacts to hover.
        float r = size * BadgeInk;
        var p0 = new V2(center.X - r * 0.44f, center.Y + r * 0.02f);
        var p1 = new V2(center.X - r * 0.11f, center.Y + r * 0.35f);
        var p2 = new V2(center.X + r * 0.46f, center.Y - r * 0.36f);

        float w = Math.Max(2f, size * 0.115f);
        var glyph = res.Brush(new Color4(1f, 1f, 1f, amount * (0.5f + 0.5f * hover)));
        dc.DrawLine(p0, p1, glyph, w, res.RoundStroke);
        dc.DrawLine(p1, p2, glyph, w, res.RoundStroke);

        dc.Transform = Matrix3x2.Identity;
        dc.PopAxisAlignedClip();
    }

    /// <summary>
    /// X badge left of the checkmark with the same ease. Glyph only; glyph differs.
    /// </summary>
    public static void PaintCloseButton(ID2D1DeviceContext dc, ResourceCache res, WidgetConfig cfg,
        float width, float height, float amount, float hover = 0f)
    {
        if (amount <= 0.01f) return;
        float s = cfg.Dpi;
        float ease = Ease(amount);
        // Derived from WidgetWindow.CloseBadgeRect, shared with the hit test.
        int logicalW = (int)MathF.Round(width / s);
        RectF lr = WidgetWindow.CloseBadgeRect(logicalW);
        float size = WidgetWindow.CheckMarkSize * s * (0.70f + 0.30f * ease);

        var center = new V2((lr.Left + lr.Width * 0.5f) * s, (lr.Top + lr.Height * 0.5f) * s);
        float sc = 0.5f + 0.5f * ease;

        dc.PushAxisAlignedClip(new RectF(0, 0, width, height), AntialiasMode.Aliased);
        dc.Transform = Matrix3x2.CreateScale(sc, sc, center);

        // ✕ glyph: two crossing round-capped strokes; only brightness reacts to hover.
        float r = size * BadgeInk;
        float w = Math.Max(2f, size * 0.115f);
        float d = r * 0.46f;
        var glyph = res.Brush(new Color4(1f, 1f, 1f, amount * (0.5f + 0.5f * hover)));
        dc.DrawLine(new V2(center.X - d, center.Y - d), new V2(center.X + d, center.Y + d), glyph, w, res.RoundStroke);
        dc.DrawLine(new V2(center.X - d, center.Y + d), new V2(center.X + d, center.Y - d), glyph, w, res.RoundStroke);

        dc.Transform = Matrix3x2.Identity;
        dc.PopAxisAlignedClip();
    }

    /// <summary>
    /// Metric switcher chevrons flanking the title. Edit mode only; boxes come from
    /// HeaderLayout via the window, so paint and hit-test are the same rects.
    /// </summary>
    public static void PaintMetricArrows(ID2D1DeviceContext dc, ResourceCache res, WidgetConfig cfg,
        float width, float height, float amount,
        System.Drawing.RectangleF prevLogical, System.Drawing.RectangleF nextLogical)
    {
        if (amount <= 0.01f) return;
        float s = cfg.Dpi;
        float ease = Ease(amount);
        float w = Math.Max(2f, 2.2f * s);

        var boxes = new[] { (prevLogical, true), (nextLogical, false) };
        foreach (var (lr, prev) in boxes)
        {
            if (lr.Width <= 0) continue;
            var center = new V2((lr.Left + lr.Width * 0.5f) * s, (lr.Top + lr.Height * 0.5f) * s);
            float r = ChevronGlyphHalf * (0.70f + 0.30f * ease) * s;
            var ink = res.Brush(new Color4(ArrowInk.R, ArrowInk.G, ArrowInk.B, ArrowInk.A * amount));
            dc.PushAxisAlignedClip(new RectF(0, 0, width, height), AntialiasMode.Aliased);
            if (prev)
            {
                dc.DrawLine(new V2(center.X + r, center.Y - r), new V2(center.X - r, center.Y), ink, w, res.RoundStroke);
                dc.DrawLine(new V2(center.X - r, center.Y), new V2(center.X + r, center.Y + r), ink, w, res.RoundStroke);
            }
            else
            {
                dc.DrawLine(new V2(center.X - r, center.Y - r), new V2(center.X + r, center.Y), ink, w, res.RoundStroke);
                dc.DrawLine(new V2(center.X + r, center.Y), new V2(center.X - r, center.Y + r), ink, w, res.RoundStroke);
            }
            dc.PopAxisAlignedClip();
        }
    }

    /// <summary>
    /// Edit-mode corner brackets. <paramref name="skipBottomLeft"/> hands the bottom-left
    /// corner to the chart's axis-label column, which sits at the card inset and must not
    /// move between locked and edit mode (docs/design.md).
    /// </summary>
    public static void PaintResizeAffordance(ID2D1DeviceContext dc, ResourceCache res, WidgetConfig cfg,
        float width, float height, float hover, bool skipBottomLeft = false)
    {
        float s = cfg.Dpi;
        float len = CornerLen * s;
        float inset = CornerInset * s;
        var brush = res.Brush(new Color4(1f, 1f, 1f, 0.34f + 0.5f * hover));
        float w = Math.Max(1.25f, 1.25f * s);

        DrawCorner(dc, brush, w, inset, inset, len, 1, 1);
        DrawCorner(dc, brush, w, width - inset, inset, len, -1, 1);
        if (!skipBottomLeft) DrawCorner(dc, brush, w, inset, height - inset, len, 1, -1);
        DrawCorner(dc, brush, w, width - inset, height - inset, len, -1, -1);
    }

    static void DrawCorner(ID2D1DeviceContext dc, ID2D1Brush brush, float w, float x, float y, float len, int sx, int sy)
    {
        dc.DrawLine(new V2(x, y), new V2(x + len * sx, y), brush, w);
        dc.DrawLine(new V2(x, y), new V2(x, y + len * sy), brush, w);
    }

    static float Ease(float t) => 1f - (1f - t) * (1f - t) * (1f - t);
}

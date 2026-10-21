using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DragDone.Core;
using DragDone.Localization;

namespace DragDone.UI;

public enum HubMode
{
    Menu,
    Busy,
    Done,
    Unchanged,
    Failed,
}

/// <summary>
/// The resolved shape of one ring: layout, radii and the size the element needs.
/// The overlay window reads this to place labels and hit areas.
/// </summary>
public sealed record MenuGeometry(
    MenuLayout Layout,
    Point Center,
    double HubRadius,
    double InnerRadius,
    double OuterRadius,
    double BadgeRadius,
    double RingWidth,
    double Reach)
{
    public bool IsOrbit => Layout == MenuLayout.Orbit;

    /// <summary>Distance from the centre at which a ring item can still be hit.</summary>
    public double HitOuter => IsOrbit ? OuterRadius + BadgeRadius : OuterRadius;

    public double HitInner => IsOrbit ? OuterRadius - BadgeRadius : HubRadius;

    public double Size => Reach * 2;
}

/// <summary>
/// The ring, drawn in one pass from the current <see cref="Palette"/> and the user's appearance
/// settings: a thin donut, a filled wheel, or a circle of floating badges.
/// </summary>
public sealed class RadialMenu : FrameworkElement
{
    /// <summary>Space around the ring reserved for outside labels.</summary>
    const double Padding = 10;

    public static readonly DependencyProperty CollapseProperty = DependencyProperty.Register(
        nameof(Collapse), typeof(double), typeof(RadialMenu), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpinProperty = DependencyProperty.Register(
        nameof(Spin), typeof(double), typeof(RadialMenu), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 = full ring, 1 = only the hub remains (used while a job runs).</summary>
    public double Collapse
    {
        get => (double)GetValue(CollapseProperty);
        set => SetValue(CollapseProperty, value);
    }

    /// <summary>Indeterminate spinner phase in degrees.</summary>
    public double Spin
    {
        get => (double)GetValue(SpinProperty);
        set => SetValue(SpinProperty, value);
    }

    IReadOnlyList<ActionDef> _items = Array.Empty<ActionDef>();
    int _hover = -1;
    bool _hubHover;
    string _title = "";
    string _subtitle = "";
    string? _backLabel;
    int _badge;
    HubMode _mode = HubMode.Menu;
    double? _progress;

    Settings _look = new();
    MenuGeometry _geo;

    public RadialMenu()
    {
        // The ring is geometry, not text flow: it must never be mirrored by an RTL parent.
        // Labels still shape and order right-to-left through FormattedText.
        FlowDirection = FlowDirection.LeftToRight;
        Shadow();
        Theme.Changed += Shadow;
        Unloaded += (_, _) => Theme.Changed -= Shadow;
        _geo = Measure();
        ApplySize();
    }

    void Shadow() => Effect = _look.BackdropBlur
        ? new DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = Theme.Current.IsDark ? 0.4 : 0.22, Color = Colors.Black, Direction = 270 }
        : null;

    /// <summary>The appearance settings this ring draws from.</summary>
    public Settings Look
    {
        get => _look;
        set
        {
            _look = value;
            _geo = Measure();
            ApplySize();
            InvalidateVisual();
        }
    }

    public MenuGeometry Geometry => _geo;

    void ApplySize()
    {
        Width = _geo.Size;
        Height = _geo.Size;
    }

    public IReadOnlyList<ActionDef> Items
    {
        get => _items;
        set
        {
            _items = value;
            _hover = -1;
            _geo = Measure();
            ApplySize();
            InvalidateVisual();
        }
    }

    public int HoverIndex
    {
        get => _hover;
        set
        {
            if (_hover == value) return;
            _hover = value;
            InvalidateVisual();
        }
    }

    public bool HubHover
    {
        get => _hubHover;
        set
        {
            if (_hubHover == value) return;
            _hubHover = value;
            InvalidateVisual();
        }
    }

    public void SetHub(string title, string subtitle, int badge)
    {
        _title = title;
        _subtitle = subtitle;
        _badge = badge;
        InvalidateVisual();
    }

    /// <summary>Non-null while a submenu is open: the hub offers a way back to the parent.</summary>
    public string? BackLabel
    {
        get => _backLabel;
        set
        {
            _backLabel = value;
            InvalidateVisual();
        }
    }

    public HubMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            InvalidateVisual();
        }
    }

    /// <summary>0..1, or null for an indeterminate spinner.</summary>
    public double? Progress
    {
        get => _progress;
        set
        {
            _progress = value;
            InvalidateVisual();
        }
    }

    public (RadialZone Zone, int Index) HitTestRing(Point p)
    {
        var g = _geo;
        var distance = (p - g.Center).Length;
        if (distance <= g.HubRadius) return (RadialZone.Hub, -1);
        if (_items.Count == 0) return (RadialZone.Outside, -1);

        if (g.IsOrbit)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var at = RadialGeometry.PointAt(g.Center, g.OuterRadius, RadialGeometry.CenterAngle(i, _items.Count));
                // Square-ish target: forgiving without stealing neighbours' clicks.
                if (Math.Abs(p.X - at.X) <= g.BadgeRadius && Math.Abs(p.Y - at.Y) <= g.BadgeRadius) return (RadialZone.Ring, i);
            }
            return (RadialZone.Outside, -1);
        }

        if (distance > g.OuterRadius + 8) return (RadialZone.Outside, -1);
        return RadialGeometry.Hit(p, g.Center, g.HubRadius, g.OuterRadius, _items.Count);
    }

    // ------------------------------------------------------------------ geometry

    MenuGeometry Measure() => Measure(_look, _items.Count);

    /// <summary>
    /// The geometry for a look and a sector count. Pure, so the layout rules are unit-tested
    /// without a window: Auto reads as a badge circle when there is room and fills in a wheel
    /// only when the ring is crowded.
    /// </summary>
    public static MenuGeometry Measure(Settings look, int itemCount)
    {
        var scale = look.Density switch
        {
            Density.Compact => 0.88,
            Density.Large => 1.14,
            _ => 1.0,
        };
        var layout = look.Layout switch
        {
            MenuLayout.Auto => itemCount switch
            {
                <= 4 => MenuLayout.Orbit,
                <= 7 => MenuLayout.Donut,
                _ => MenuLayout.Pie,
            },
            var explicitLayout => explicitLayout,
        };

        var radius = Math.Clamp(look.MenuDiameter, 240, 520) / 2;
        var labelsOutside = layout != MenuLayout.Pie;
        var reach = radius + Padding + (labelsOutside ? 62 : 0);
        var center = new Point(reach, reach);

        return layout switch
        {
            MenuLayout.Orbit => new MenuGeometry(layout, center, radius * 0.34, 0, radius * 0.78, radius * 0.19 * scale, 0, reach),
            MenuLayout.Donut => Donut(center, radius, scale, reach),
            _ => new MenuGeometry(layout, center, radius * 0.30, radius * 0.30, radius, 0, radius * 0.70, reach),
        };
    }

    static MenuGeometry Donut(Point center, double radius, double scale, double reach)
    {
        var width = Math.Max(56, radius * 0.30 * scale);
        return new MenuGeometry(MenuLayout.Donut, center, Math.Max(34, radius * 0.30), radius - width, radius, 0, width, reach);
    }

    // ------------------------------------------------------------------ rendering

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var palette = Theme.Current;
        _geo = Measure();

        if (Collapse < 1)
        {
            dc.PushOpacity(1 - Collapse);
            if (_geo.IsOrbit) DrawOrbit(dc, palette, dpi);
            else DrawSectors(dc, palette, dpi);
            dc.Pop();
        }
        DrawHub(dc, palette, dpi);
    }

    void DrawSectors(DrawingContext dc, Palette palette, double dpi)
    {
        var n = _items.Count;
        if (n == 0) return;
        var g = _geo;
        var span = RadialGeometry.Span(n);
        var filled = _look.ThemePreset is not ("graphite" or "terminal" or "contrast");
        var gradient = new LinearGradientBrush(palette.WheelFill, palette.WheelFillAlt, 90);
        gradient.Freeze();

        for (var i = 0; i < n; i++)
        {
            var item = _items[i];
            var hovered = i == _hover && item.Enabled;
            var mid = RadialGeometry.CenterAngle(i, n);
            var grow = hovered ? _look.HoverGrow * 5 : 0;
            var outer = g.OuterRadius + grow;
            var inner = Math.Max(g.HubRadius + 1, g.InnerRadius - grow * 0.5);
            var geo = Wedge(mid - span / 2, mid + span / 2, inner, outer, _look.Gap, g);

            Brush fill = !item.Enabled
                ? Theme.Brush(Blend(palette.WheelFill, palette.Surface, 0.55))
                : hovered ? Theme.Brush(palette.Accent) : gradient;
            var edge = new Pen(Theme.Brush(hovered ? palette.Accent : palette.WheelEdge), Math.Max(0, _look.FrameThickness));
            dc.DrawGeometry(filled || hovered ? fill : null, edge, geo);

            // Outside labels sit on their own surface pill, so hover can fill that pill instead.
            var onAccent = Theme.On(palette.Accent);
            var text = Theme.Brush(!item.Enabled ? palette.TextDim : hovered ? onAccent : palette.Text);
            DrawLabel(dc, item, mid, outer, text, dpi, span, g.Layout == MenuLayout.Pie, hovered);
        }
    }

    void DrawOrbit(DrawingContext dc, Palette palette, double dpi)
    {
        var n = _items.Count;
        if (n == 0) return;
        var g = _geo;
        var span = RadialGeometry.Span(n);
        var pen = new Pen(Theme.Brush(palette.WheelEdge), Math.Max(0, _look.FrameThickness));

        // A hairline track ties the badges together without filling the middle.
        if (_look.Gap <= 6) dc.DrawEllipse(null, new Pen(Theme.Brush(palette.RingTrack), 1), g.Center, g.OuterRadius, g.OuterRadius);

        for (var i = 0; i < n; i++)
        {
            var item = _items[i];
            var hovered = i == _hover && item.Enabled;
            var mid = RadialGeometry.CenterAngle(i, n);
            var at = RadialGeometry.PointAt(g.Center, g.OuterRadius, mid);
            var r = g.BadgeRadius * (hovered ? 1 + _look.HoverGrow * 0.07 : 1);

            Brush fill = !item.Enabled
                ? Theme.Brush(Blend(palette.WheelFill, palette.Surface, 0.6))
                : hovered ? Theme.Brush(palette.Accent) : Theme.Brush(palette.WheelFill);
            dc.DrawEllipse(fill, pen, at, r, r);

            var text = Theme.Brush(!item.Enabled ? palette.TextDim : hovered ? Theme.On(palette.Accent) : palette.Text);
            DrawLabel(dc, item, mid, g.OuterRadius, text, dpi, span, true, hovered);
        }
    }

    /// <summary>
    /// Draws one sector's content as a single block. Outside labels get a small surface pill:
    /// the ring floats over an unknown desktop, so bare text would vanish on some wallpapers.
    /// The pill is placed by its own half-extent, so it clears the rim at every angle.
    /// </summary>
    void DrawLabel(DrawingContext dc, ActionDef item, double angle, double outer, Brush brush, double dpi, double span, bool inside, bool hovered)
    {
        // Inside a filled wheel the text sits in the band between hub and rim; in the orbit layout
        // it sits in the badge itself. Both need a tighter measure than an outside pill: the
        // wedge and the badge are the only room the text is allowed to take.
        var at = _geo.Center;
        if (inside)
        {
            var rad = (angle - 90) * Math.PI / 180;
            var radius = _geo.Layout == MenuLayout.Orbit
                ? _geo.OuterRadius
                : (_geo.InnerRadius + _geo.OuterRadius) / 2;
            at = new Point(_geo.Center.X + Math.Cos(rad) * radius, _geo.Center.Y + Math.Sin(rad) * radius);
        }

        var arcRoom = 2 * Math.PI * (_geo.Layout == MenuLayout.Orbit ? _geo.BadgeRadius : Math.Max(60, _geo.OuterRadius)) * span / 360 - 10;
        var insideRoom = _geo.Layout == MenuLayout.Orbit ? Math.Max(40, _geo.BadgeRadius * 1.75) : 92;
        var maxWidth = Math.Max(38, Math.Min(inside ? insideRoom : 132, arcRoom));

        // A badge is narrower than a wedge, so the format name is set smaller there: a four-letter
        // name like WebP has to fit inside a circle without turning into an ellipsis.
        var formatSize = (inside && _geo.Layout == MenuLayout.Orbit ? 13.5 : item.Label.Length > 5 ? 14 : 17) * Scale;

        FormattedText? glyph = null;
        FormattedText label;
        if (item.Style == ActionStyle.Format)
            label = Text(item.Label, formatSize, FontWeights.Bold, brush, dpi, maxWidth, Theme.DisplayFont);
        else if (!ShowsName(item) || item.GlyphOnly)
            label = Text(item.Glyph, 19 * Scale, FontWeights.Normal, brush, dpi, 44, Theme.IconFont);
        else
        {
            glyph = Text(item.Glyph, 18 * Scale, FontWeights.Normal, brush, dpi, 44, Theme.IconFont);
            label = Text(item.Label, 12 * Scale, FontWeights.SemiBold, brush, dpi, maxWidth, Theme.UiFont, maxLines: 2);
        }

        var width = item.Style == ActionStyle.Format
            ? Math.Min(maxWidth, Natural(item.Label, formatSize, FontWeights.Bold, Theme.DisplayFont, dpi))
            : !ShowsName(item) || item.GlyphOnly
                ? Math.Min(44, Natural(item.Glyph, 19 * Scale, FontWeights.Normal, Theme.IconFont, dpi))
                : Math.Max(Natural(item.Glyph, 18 * Scale, FontWeights.Normal, Theme.IconFont, dpi),
                           Math.Min(maxWidth, Natural(item.Label, 12 * Scale, FontWeights.SemiBold, Theme.UiFont, dpi)));
        var contentHeight = (glyph?.Height ?? 0) + (glyph is null ? 0 : 2) + label.Height;

        // A label that opens a submenu carries a dot row. It is part of the block, so the pill is
        // sized around it instead of the dots landing on the pill's border.
        var more = item.HasChildren && !item.GlyphOnly;
        var moreRoom = more ? 9.0 : 0.0;
        var height = contentHeight + moreRoom;

        var padX = inside ? 0 : 7;
        var padY = inside ? 0 : 4;

        if (!inside)
        {
            var rad = (angle - 90) * Math.PI / 180;
            var dx = Math.Cos(rad);
            var dy = Math.Sin(rad);
            // Half the block's extent along the outward direction: clears the rim at any angle.
            var extent = Math.Abs(dx) * (width / 2 + padX) + Math.Abs(dy) * (height / 2 + padY);
            var distance = outer + 8 + extent;
            at = new Point(_geo.Center.X + dx * distance, _geo.Center.Y + dy * distance);
        }

        var turn = 0.0;
        if (!inside && _look.RotatedLabels)
        {
            // Readable at every position: no label is ever drawn upside down.
            turn = angle > 90 && angle < 270 ? angle + 180 : angle;
            dc.PushTransform(new RotateTransform(turn, at.X, at.Y));
        }

        if (!inside)
        {
            var box = new Rect(at.X - width / 2 - padX, at.Y - height / 2 - padY, width + padX * 2, height + padY * 2);
            var fill = hovered ? Theme.Brush(Theme.Current.Accent) : Theme.Brush(Theme.Current.Surface);
            dc.DrawRoundedRectangle(fill, new Pen(Theme.Brush(Theme.Current.WheelEdge), 1), box, 7, 7);
        }

        var top = at.Y - height / 2;
        if (glyph is not null)
        {
            Put(dc, glyph, at.X, top);
            Put(dc, label, at.X, top + glyph.Height + 2);
        }
        else Put(dc, label, at.X, top);
        if (more) DrawMore(dc, at.X, top + contentHeight + 5, brush);

        if (turn != 0) dc.Pop();
    }

    /// <summary>Width the text needs when it is not constrained by the sector's wrapping box.</summary>
    static double Natural(string text, double size, FontWeight weight, FontFamily family, double dpi)
    {
        var free = new FormattedText(text, CultureInfo.CurrentUICulture, Loc.Flow,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.Transparent, dpi)
        {
            MaxTextWidth = 4000,
        };
        return free.WidthIncludingTrailingWhitespace;
    }

    bool ShowsName(ActionDef item) => _look.Labels switch
    {
        LabelStyle.Icons => false,
        LabelStyle.Text or LabelStyle.Labels => true,
        // Auto: names outside the ring, icons alone when the sector is too narrow to read.
        _ => item.Style != ActionStyle.Format && _geo.Layout != MenuLayout.Orbit || item.Style == ActionStyle.Format,
    };

    double Scale => _look.Density switch
    {
        Density.Compact => 0.9,
        Density.Large => 1.14,
        _ => 1.0,
    };

    /// <summary>A small "more" dot row under labels that open a submenu.</summary>
    static void DrawMore(DrawingContext dc, double x, double y, Brush brush)
    {
        for (var k = -1; k <= 1; k++) dc.DrawEllipse(brush, null, new Point(x + k * 5, y), 1.4, 1.4);
    }

    void DrawHub(DrawingContext dc, Palette palette, double dpi)
    {
        var g = _geo;
        var accent = Theme.Brush(palette.Accent);
        var rim = new Pen(Theme.Brush(palette.WheelEdge), 1);

        switch (_mode)
        {
            case HubMode.Busy:
            {
                dc.DrawEllipse(Theme.Brush(palette.WheelFill), rim, g.Center, g.HubRadius, g.HubRadius);
                var track = new Pen(Theme.Brush(palette.RingTrack), 4);
                dc.DrawEllipse(null, track, g.Center, g.HubRadius - 7, g.HubRadius - 7);
                var pen = new Pen(accent, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (_progress is { } p && p > 0.001)
                {
                    dc.DrawGeometry(null, pen, Arc(0, 360 * Math.Min(p, 0.9999), g.HubRadius - 7, g.Center));
                    var pct = Text(Loc.Digits(((int)Math.Round(p * 100)).ToString(CultureInfo.InvariantCulture)) + (Loc.IsRtl ? "٪" : "%"),
                        15, FontWeights.SemiBold, Theme.Brush(palette.Text), dpi, 80, Theme.DisplayFont);
                    Put(dc, pct, g.Center.X, g.Center.Y - pct.Height / 2);
                }
                else
                {
                    dc.DrawGeometry(null, pen, Arc(Spin, Spin + 90, g.HubRadius - 7, g.Center));
                }
                return;
            }
            case HubMode.Done:
            case HubMode.Unchanged:
            case HubMode.Failed:
            {
                var fill = _mode switch
                {
                    HubMode.Done => accent,
                    HubMode.Failed => Theme.Brush(palette.Danger),
                    _ => Theme.Brush(palette.WheelFill),
                };
                dc.DrawEllipse(fill, rim, g.Center, g.HubRadius, g.HubRadius);
                var glyph = _mode == HubMode.Failed ? Glyphs.Error : Glyphs.Check;
                var fg = _mode == HubMode.Done ? Theme.Brush(Theme.On(palette.Accent)) : Theme.Brush(palette.Text);
                var t = Text(glyph, 30, FontWeights.Normal, fg, dpi, 60, Theme.IconFont);
                Put(dc, t, g.Center.X, g.Center.Y - t.Height / 2);
                return;
            }
        }

        var back = _backLabel is not null;
        var active = _hubHover && back;
        dc.DrawEllipse(active ? accent : Theme.Brush(palette.WheelFill), new Pen(Theme.Brush(active ? palette.Accent : palette.WheelEdge), 1.2), g.Center, g.HubRadius, g.HubRadius);
        var title = Theme.Brush(active ? Theme.On(palette.Accent) : palette.Text);

        if (back)
        {
            var arrow = Text(Loc.IsRtl ? "\uE72A" : Glyphs.Back, 14, FontWeights.Normal, title, dpi, 40, Theme.IconFont);
            var lbl = Text(_backLabel!, 12, FontWeights.SemiBold, title, dpi, g.HubRadius * 1.5, Theme.UiFont, maxLines: 2);
            var h = arrow.Height + 2 + lbl.Height;
            Put(dc, arrow, g.Center.X, g.Center.Y - h / 2);
            Put(dc, lbl, g.Center.X, g.Center.Y - h / 2 + arrow.Height + 2);
        }
        else
        {
            var width = g.HubRadius * 1.7;
            // Shrink long titles ("ملفات متنوعة", "Documents") before resorting to an ellipsis.
            var titleSize = new[] { 15.0, 13.5, 12.0 }.FirstOrDefault(
                size => Text(_title, size, FontWeights.Bold, title, dpi, 1000, Theme.DisplayFont).WidthIncludingTrailingWhitespace <= width, 12.0);
            var t = Text(_title, titleSize, FontWeights.Bold, title, dpi, width, Theme.DisplayFont);
            var s = Text(_subtitle, 11, FontWeights.Normal, Theme.Brush(palette.TextSoft), dpi, width, Theme.UiFont);
            var h = t.Height + s.Height;
            Put(dc, t, g.Center.X, g.Center.Y - h / 2);
            Put(dc, s, g.Center.X, g.Center.Y - h / 2 + t.Height);
        }

        if (_badge > 1)
        {
            var at = RadialGeometry.PointAt(g.Center, g.HubRadius - 6, 45);
            dc.DrawEllipse(Theme.Brush(palette.Badge), new Pen(Brushes.White, 1.5), at, 11, 11);
            var b = Text(Loc.Num(_badge), 11, FontWeights.Bold, Brushes.White, dpi, 30, Theme.UiFont);
            Put(dc, b, at.X, at.Y - b.Height / 2);
        }
    }

    static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        Math.Max(a.A, b.A),
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// Draws text centred on <paramref name="centerX"/>. Text is laid out (and centred) inside a
    /// MaxTextWidth box whose origin is its left edge, because this element is always LTR;
    /// right-to-left labels shape and order correctly inside that box.
    /// </summary>
    static void Put(DrawingContext dc, FormattedText ft, double centerX, double top) =>
        dc.DrawText(ft, new Point(centerX - ft.MaxTextWidth / 2, top));

    static FormattedText Text(string s, double size, FontWeight weight, Brush brush, double dpi, double maxWidth, FontFamily family, int maxLines = 1)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, Loc.Flow,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, brush, dpi)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = Math.Max(10, maxWidth),
            MaxLineCount = maxLines,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return ft;
    }

    // ------------------------------------------------------------------ geometry helpers

    Geometry Wedge(double fromDeg, double toDeg, double inner, double outer, double gapPx, MenuGeometry g)
    {
        // Straight, equal-width gaps between sectors: convert the pixel gap to an angle per radius.
        var go = gapPx / 2 / outer * 180 / Math.PI;
        var gi = gapPx / 2 / Math.Max(1, inner) * 180 / Math.PI;
        var o1 = RadialGeometry.PointAt(g.Center, outer, fromDeg + go);
        var o2 = RadialGeometry.PointAt(g.Center, outer, toDeg - go);
        var i2 = RadialGeometry.PointAt(g.Center, inner, toDeg - gi);
        var i1 = RadialGeometry.PointAt(g.Center, inner, fromDeg + gi);
        var large = toDeg - fromDeg > 180;
        var fig = new PathFigure { StartPoint = o1, IsClosed = true };
        fig.Segments.Add(new ArcSegment(o2, new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(i2, true));
        fig.Segments.Add(new ArcSegment(i1, new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
        var geo = new PathGeometry(new[] { fig });
        geo.Freeze();
        return geo;
    }

    static Geometry Arc(double fromDeg, double toDeg, double r, Point center)
    {
        var a = RadialGeometry.PointAt(center, r, fromDeg);
        var b = RadialGeometry.PointAt(center, r, toDeg);
        var fig = new PathFigure { StartPoint = a, IsClosed = false };
        fig.Segments.Add(new ArcSegment(b, new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true));
        var g = new PathGeometry(new[] { fig });
        g.Freeze();
        return g;
    }
}

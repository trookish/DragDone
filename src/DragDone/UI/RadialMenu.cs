using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

/// <summary>The outline a badge layout draws for each item.</summary>
public enum BadgeShape
{
    None,
    Circle,
    Hexagon,
    Tile,
    Diamond,
}

/// <summary>
/// The resolved shape of one ring: layout, radii, the arc it covers and the size the element
/// needs. The overlay window reads this to place labels and hit areas.
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
    /// <summary>Number of sectors the geometry was measured for.</summary>
    public int Count { get; init; }

    /// <summary>Where the first slot of a partial ring begins (degrees, 0 = up, clockwise).</summary>
    public double StartAngle { get; init; }

    /// <summary>How much of the circle the ring covers; 360 for every layout but the fan.</summary>
    public double Sweep { get; init; } = 360;

    /// <summary>Partial rings run right to left in Arabic, so the first item sits where reading starts.</summary>
    public bool Mirrored { get; init; }

    public BadgeShape Badge { get; init; }

    /// <summary>True for every layout made of floating badges rather than joined sectors.</summary>
    public bool IsBadges => Badge != BadgeShape.None;

    /// <summary>Kept for older callers: any badge layout.</summary>
    public bool IsOrbit => IsBadges;

    public bool IsFull => Sweep >= 359.9;

    /// <summary>Distance from the centre at which a ring item can still be hit.</summary>
    public double HitOuter => IsBadges ? OuterRadius + BadgeRadius : OuterRadius;

    public double HitInner => IsBadges ? OuterRadius - BadgeRadius : HubRadius;

    public double Size => Reach * 2;

    public double Span => Sweep / Math.Max(1, Count);

    /// <summary>Centre angle of sector <paramref name="index"/>.</summary>
    public double Mid(int index)
    {
        if (IsFull) return RadialGeometry.CenterAngle(index, Count);
        var slot = Mirrored ? Count - 1 - index : index;
        return (StartAngle + Span * (slot + 0.5)) % 360;
    }

    /// <summary>The sector under an angle, or -1 when the angle falls outside a partial ring.</summary>
    public int IndexAt(double angle)
    {
        if (Count <= 0) return -1;
        if (IsFull) return RadialGeometry.IndexAt(angle, Count);
        var rel = ((angle - StartAngle) % 360 + 360) % 360;
        if (rel > Sweep) return -1;
        var slot = Math.Min(Count - 1, (int)Math.Floor(rel / Span));
        return Mirrored ? Count - 1 - slot : slot;
    }
}

/// <summary>
/// The ring, drawn in one pass from the current <see cref="Palette"/> and the user's appearance
/// settings: a donut, a filled wheel, petals, a fan, or a circle of floating badges. It also owns
/// its motion (the opening animation and the smoothed hover state), so every surface that shows
/// a ring moves the same way: the overlay, the settings preview and the welcome window.
/// </summary>
public sealed class RadialMenu : FrameworkElement
{
    /// <summary>Space around the ring reserved for outside labels.</summary>
    const double Padding = 10;

    /// <summary>Room for hover effects that reach past the rim of a wheel (lift, glow, pulse).</summary>
    const double HoverRoom = 12;

    public static readonly DependencyProperty CollapseProperty = DependencyProperty.Register(
        nameof(Collapse), typeof(double), typeof(RadialMenu), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpinProperty = DependencyProperty.Register(
        nameof(Spin), typeof(double), typeof(RadialMenu), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Progress of the opening animation: 0 = not yet shown, 1 = settled.</summary>
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(RadialMenu), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public double Reveal
    {
        get => (double)GetValue(RevealProperty);
        set => SetValue(RevealProperty, value);
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

    // Smoothed hover: each sector eases toward 1 while hovered and back to 0 when left.
    double[] _hoverAmount = Array.Empty<double>();
    double _hubAmount;
    double _pulse;
    bool _ticking;
    TimeSpan _lastFrame;

    Settings _look = new();
    MenuGeometry _geo;

    public RadialMenu()
    {
        // The ring is geometry, not text flow: it must never be mirrored by an RTL parent.
        // Labels still shape and order right-to-left through FormattedText.
        FlowDirection = FlowDirection.LeftToRight;
        Shadow();
        // Subscribe only while on screen: a ring built for a preview and then dropped would
        // otherwise stay reachable from the static event for the life of the process.
        Loaded += (_, _) =>
        {
            Theme.Changed -= Shadow;
            Theme.Changed += Shadow;
            Shadow();
        };
        Unloaded += (_, _) =>
        {
            Theme.Changed -= Shadow;
            StopTicking();
        };
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
            Shadow();
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
            _hoverAmount = new double[value.Count];
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
            Animate();
        }
    }

    public bool HubHover
    {
        get => _hubHover;
        set
        {
            if (_hubHover == value) return;
            _hubHover = value;
            Animate();
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
            Animate();
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

        if (g.IsBadges)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var at = RadialGeometry.PointAt(g.Center, g.OuterRadius, g.Mid(i));
                // Square-ish target: forgiving without stealing neighbours' clicks.
                if (Math.Abs(p.X - at.X) <= g.BadgeRadius && Math.Abs(p.Y - at.Y) <= g.BadgeRadius) return (RadialZone.Ring, i);
            }
            return (RadialZone.Outside, -1);
        }

        if (distance > g.OuterRadius + 8) return (RadialZone.Outside, -1);
        var index = g.IndexAt(RadialGeometry.AngleOf(p - g.Center));
        return index < 0 ? (RadialZone.Outside, -1) : (RadialZone.Ring, index);
    }

    // ------------------------------------------------------------------ motion

    /// <summary>False when the user, or Windows if they follow it, turned animation off.</summary>
    public bool MotionEnabled => !(_look.FollowSystemMotion && !SystemParameters.ClientAreaAnimation);

    OpenAnimation Opening => MotionEnabled && _look.AnimationMs > 0 ? _look.Animation : OpenAnimation.None;

    double HoverDuration => MotionEnabled ? _look.HoverMs : 0;

    /// <summary>Length of the opening animation as it will actually play, in milliseconds.</summary>
    public double OpeningMs => Opening == OpenAnimation.None ? 0 : _look.AnimationMs;

    /// <summary>
    /// Plays the opening animation. A transition (tab or submenu change) plays a shorter version
    /// of it, so moving around a ring stays quick.
    /// </summary>
    public void PlayOpen(bool transition = false)
    {
        BeginAnimation(RevealProperty, null);
        Reveal = 1;
        if (transition && !_look.AnimateRingChanges) return;
        var ms = OpeningMs * (transition ? 0.6 : 1);
        if (ms < 1) return;
        // The end value is the base value and the animation does not hold it, so nothing stays
        // attached afterwards to pin the property.
        BeginAnimation(RevealProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Jumps every animation to its end state: for snapshots and still previews.</summary>
    public void Settle()
    {
        BeginAnimation(RevealProperty, null);
        Reveal = 1;
        StopTicking();
        if (_hoverAmount.Length != _items.Count) _hoverAmount = new double[_items.Count];
        for (var i = 0; i < _hoverAmount.Length; i++) _hoverAmount[i] = HoverTarget(i);
        _hubAmount = HubTarget;
        InvalidateVisual();
    }

    double HoverTarget(int i) => i == _hover && i < _items.Count && _items[i].Enabled ? 1 : 0;

    double HubTarget => _hubHover && _backLabel is not null ? 1 : 0;

    bool Pulsing => _look.Hover == HoverEffect.Pulse && _hover >= 0 && HoverDuration > 0;

    /// <summary>Starts the frame loop if anything has somewhere to go; snaps when it cannot run.</summary>
    void Animate()
    {
        if (_hoverAmount.Length != _items.Count) _hoverAmount = new double[_items.Count];
        // Rings that are not on screen (previews, snapshots) never get frames, and instant
        // settings do not need them: both settle at once.
        if (HoverDuration <= 0 || PresentationSource.FromVisual(this) is null)
        {
            for (var i = 0; i < _hoverAmount.Length; i++) _hoverAmount[i] = HoverTarget(i);
            _hubAmount = HubTarget;
            StopTicking();
            InvalidateVisual();
            return;
        }
        if (_ticking) return;
        _ticking = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    void StopTicking()
    {
        if (!_ticking) return;
        _ticking = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        // Rendering can fire more than once per frame; only a new frame advances the state.
        if (now == _lastFrame && now != TimeSpan.Zero) return;
        var dt = _lastFrame == TimeSpan.Zero ? 16 : Math.Min(50, (now - _lastFrame).TotalMilliseconds);
        _lastFrame = now;

        var moving = false;
        var duration = HoverDuration;
        for (var i = 0; i < _hoverAmount.Length; i++)
        {
            var next = Motion.Approach(_hoverAmount[i], HoverTarget(i), dt, duration);
            if (next != _hoverAmount[i]) moving = true;
            _hoverAmount[i] = next;
        }
        var hub = Motion.Approach(_hubAmount, HubTarget, dt, duration);
        if (hub != _hubAmount) moving = true;
        _hubAmount = hub;

        if (Pulsing)
        {
            _pulse = (_pulse + dt / 1000) % 1000;
            moving = true;
        }
        if (moving) InvalidateVisual();
        else StopTicking();
    }

    double Amount(int i) => i < _hoverAmount.Length ? _hoverAmount[i] : 0;

    /// <summary>The strongest hover anywhere on the ring; the spotlight dims the rest by it.</summary>
    double AnyHover()
    {
        var max = 0.0;
        foreach (var a in _hoverAmount) max = Math.Max(max, a);
        return max;
    }

    /// <summary>Strength of the hover effects, from the hover intensity slider.</summary>
    double Intensity => _look.HoverGrow;

    /// <summary>Breathing phase for the pulse effect, 0..1.</summary>
    double PulseWave => 0.5 + 0.5 * Math.Sin(_pulse * 2 * Math.PI * 1.4 - Math.PI / 2);

    /// <summary>How far an effect pushes a wedge's rim out at full hover, in pixels.</summary>
    double GrowAt(double h) => _look.Hover switch
    {
        HoverEffect.Grow => 5 * Intensity * h,
        HoverEffect.Pulse => (2 + 5 * PulseWave) * Intensity * h,
        _ => 0,
    };

    double LiftAt(double h) => _look.Hover == HoverEffect.Lift ? 9 * Intensity * h : 0;

    double MagnifyAt(double h) => _look.Hover == HoverEffect.Magnify ? 1 + 0.22 * Intensity * h : 1;

    /// <summary>How much of the accent a hovered sector takes on.</summary>
    double FillAt(double h) => _look.Hover switch
    {
        HoverEffect.Outline => 0,
        HoverEffect.Tint => 0.32 * h,
        _ => h,
    };

    // ------------------------------------------------------------------ geometry

    MenuGeometry Measure() => Measure(_look, _items.Count);

    /// <summary>The layout a look resolves to for a number of sectors.</summary>
    public static MenuLayout ResolveLayout(MenuLayout layout, int itemCount) => layout switch
    {
        MenuLayout.Auto => itemCount switch
        {
            <= 4 => MenuLayout.Orbit,
            <= 7 => MenuLayout.Donut,
            _ => MenuLayout.Pie,
        },
        var explicitLayout => explicitLayout,
    };

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
        var layout = ResolveLayout(look.Layout, itemCount);

        var radius = Math.Clamp(look.MenuDiameter, 240, 520) / 2;
        // Wheels keep their labels inside; everything else needs room around the rim for outside
        // labels or for badges that grow and lift.
        var wheel = layout is MenuLayout.Pie or MenuLayout.Petals;
        var reach = radius + Padding + (wheel ? HoverRoom : 62);
        var center = new Point(reach, reach);

        MenuGeometry Badges(BadgeShape shape, double size) =>
            new(layout, center, radius * 0.34, 0, radius * 0.78, radius * size * scale, 0, reach) { Badge = shape };

        var geo = layout switch
        {
            MenuLayout.Orbit => Badges(BadgeShape.Circle, 0.19),
            MenuLayout.Honeycomb => Badges(BadgeShape.Hexagon, 0.19),
            MenuLayout.Tiles => Badges(BadgeShape.Tile, 0.18),
            MenuLayout.Diamonds => Badges(BadgeShape.Diamond, 0.17),
            MenuLayout.Donut => Donut(layout, center, radius, scale, reach),
            MenuLayout.Fan => Donut(layout, center, radius, scale, reach) with
            {
                StartAngle = 270,
                Sweep = 180,
                Mirrored = Loc.IsRtl,
            },
            MenuLayout.Petals => new MenuGeometry(layout, center, radius * 0.28, radius * 0.30, radius, 0, radius * 0.70, reach),
            _ => new MenuGeometry(MenuLayout.Pie, center, radius * 0.30, radius * 0.30, radius, 0, radius * 0.70, reach),
        };
        return geo with { Count = itemCount };
    }

    static MenuGeometry Donut(MenuLayout layout, Point center, double radius, double scale, double reach)
    {
        var width = Math.Max(56, radius * 0.30 * scale);
        return new MenuGeometry(layout, center, Math.Max(34, radius * 0.30), radius - width, radius, 0, width, reach);
    }

    // ------------------------------------------------------------------ rendering

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var palette = Theme.Current;
        _geo = Measure();

        var anim = Opening;
        var t = anim == OpenAnimation.None ? 1 : Motion.Clamp01(Reveal);
        var pushes = PushWhole(dc, anim, t);

        if (Collapse < 1)
        {
            dc.PushOpacity(1 - Collapse);
            if (_geo.IsBadges) DrawBadges(dc, palette, dpi, anim, t);
            else DrawSectors(dc, palette, dpi, anim, t);
            dc.Pop();
        }

        // Per-sector animations grow out of the hub, so the hub arrives first.
        var hubPushes = 0;
        if (Motion.IsPerSector(anim) && t < 1)
        {
            var hp = Motion.HubProgress(anim, t);
            dc.PushOpacity(hp);
            var s = 0.6 + 0.4 * Motion.BackOut(hp);
            dc.PushTransform(new ScaleTransform(s, s, _geo.Center.X, _geo.Center.Y));
            hubPushes = 2;
        }
        DrawHub(dc, palette, dpi);
        for (var i = 0; i < hubPushes + pushes; i++) dc.Pop();
    }

    /// <summary>Applies the whole-ring part of an opening animation; returns how many pushes to pop.</summary>
    int PushWhole(DrawingContext dc, OpenAnimation anim, double t)
    {
        if (t >= 1 || anim == OpenAnimation.None || Motion.IsPerSector(anim)) return 0;
        var c = _geo.Center;
        double scale = 1, rotate = 0, dy = 0, opacity;
        switch (anim)
        {
            case OpenAnimation.Fade:
                opacity = Motion.CubicOut(t);
                break;
            case OpenAnimation.Pop:
                scale = 0.82 + 0.18 * Motion.BackOut(t, 1.4);
                opacity = Motion.Clamp01(t * 2.2);
                break;
            case OpenAnimation.Zoom:
                scale = 0.3 + 0.7 * Motion.CubicOut(t);
                opacity = Motion.Clamp01(t * 1.6);
                break;
            case OpenAnimation.Spin:
                rotate = -120 * (1 - Motion.CubicOut(t));
                scale = 0.6 + 0.4 * Motion.CubicOut(t);
                opacity = Motion.Clamp01(t * 2);
                break;
            case OpenAnimation.Drop:
                dy = -44 * (1 - Motion.BounceOut(t));
                opacity = Motion.Clamp01(t * 3);
                break;
            case OpenAnimation.Elastic:
                scale = 0.45 + 0.55 * Motion.ElasticOut(t);
                opacity = Motion.Clamp01(t * 3);
                break;
            default:
                return 0;
        }
        var group = new TransformGroup();
        if (scale != 1) group.Children.Add(new ScaleTransform(scale, scale, c.X, c.Y));
        if (rotate != 0) group.Children.Add(new RotateTransform(rotate, c.X, c.Y));
        if (dy != 0) group.Children.Add(new TranslateTransform(0, dy));
        group.Freeze();
        dc.PushOpacity(opacity);
        dc.PushTransform(group);
        return 2;
    }

    /// <summary>Applies the per-sector part of an opening animation around the sector's own centre.</summary>
    int PushSector(DrawingContext dc, OpenAnimation anim, double t, int index, Point sectorCenter, double angle)
    {
        if (t >= 1 || !Motion.IsPerSector(anim)) return 0;
        var p = Motion.SectorProgress(anim, t, index, _items.Count);
        var group = new TransformGroup();
        double opacity;
        switch (anim)
        {
            case OpenAnimation.Cascade:
            {
                var s = 0.6 + 0.4 * Motion.BackOut(p);
                group.Children.Add(new ScaleTransform(s, s, sectorCenter.X, sectorCenter.Y));
                opacity = Motion.CubicOut(p);
                break;
            }
            case OpenAnimation.Bloom:
            {
                // Slides out from the hub along the sector's own direction.
                var back = (1 - Motion.CubicOut(p)) * _geo.OuterRadius * 0.55;
                var rad = (angle - 90) * Math.PI / 180;
                var s = 0.4 + 0.6 * Motion.CubicOut(p);
                group.Children.Add(new ScaleTransform(s, s, sectorCenter.X, sectorCenter.Y));
                group.Children.Add(new TranslateTransform(-Math.Cos(rad) * back, -Math.Sin(rad) * back));
                opacity = Motion.Clamp01(p * 1.5);
                break;
            }
            default: // Ripple
            {
                var s = 0.2 + 0.8 * Motion.BackOut(p, 2.4);
                group.Children.Add(new ScaleTransform(s, s, sectorCenter.X, sectorCenter.Y));
                opacity = Motion.Clamp01(p * 2);
                break;
            }
        }
        group.Freeze();
        dc.PushOpacity(opacity);
        dc.PushTransform(group);
        return 2;
    }

    /// <summary>Moves a hovered sector away from the hub for the lift effect.</summary>
    int PushLift(DrawingContext dc, double angle, double h)
    {
        var lift = LiftAt(h);
        if (lift <= 0.05) return 0;
        var rad = (angle - 90) * Math.PI / 180;
        dc.PushTransform(new TranslateTransform(Math.Cos(rad) * lift, Math.Sin(rad) * lift));
        return 1;
    }

    /// <summary>The spotlight dims every sector except the one being held.</summary>
    int PushSpotlight(DrawingContext dc, double h, double any)
    {
        if (_look.Hover != HoverEffect.Spotlight || any <= 0.01) return 0;
        var dim = 1 - 0.55 * Math.Min(1, Intensity) * any * (1 - h);
        if (dim >= 0.999) return 0;
        dc.PushOpacity(dim);
        return 1;
    }

    void DrawSectors(DrawingContext dc, Palette palette, double dpi, OpenAnimation anim, double t)
    {
        var n = _items.Count;
        if (n == 0) return;
        var g = _geo;
        var span = g.Span;
        var filled = _look.ThemePreset is not ("graphite" or "terminal" or "contrast");
        var gradient = new LinearGradientBrush(palette.WheelFill, palette.WheelFillAlt, 90);
        gradient.Freeze();
        var any = AnyHover();
        var petals = g.Layout == MenuLayout.Petals;
        var inside = g.Layout is MenuLayout.Pie or MenuLayout.Petals;

        for (var i = 0; i < n; i++)
        {
            var item = _items[i];
            var h = item.Enabled ? Amount(i) : 0;
            var mid = g.Mid(i);
            var grow = GrowAt(h);
            var outer = g.OuterRadius + grow;
            var inner = Math.Max(g.HubRadius + 1, g.InnerRadius - grow * 0.5);
            var sectorCenter = RadialGeometry.PointAt(g.Center, (inner + outer) / 2, mid);

            var pops = PushSector(dc, anim, t, i, sectorCenter, mid);
            pops += PushSpotlight(dc, h, any);
            pops += PushLift(dc, mid, h);

            var geo = petals
                ? Petal(mid, span, inner, outer, _look.Gap, g)
                : Wedge(mid - span / 2, mid + span / 2, inner, outer, _look.Gap, g);

            var fillAmount = FillAt(h);
            Brush? fill = !item.Enabled
                ? Theme.Brush(Blend(palette.WheelFill, palette.Surface, 0.55))
                : fillAmount <= 0.001
                    ? filled ? gradient : null
                    : filled
                        ? Theme.Brush(Blend(palette.WheelFill, palette.Accent, fillAmount))
                        : Theme.Brush(WithAlpha(palette.Accent, fillAmount));

            if (_look.Hover == HoverEffect.Glow && h > 0.01) DrawGlow(dc, geo, palette.Accent, h);

            var outline = _look.Hover == HoverEffect.Outline;
            var edgeColor = Blend(palette.WheelEdge, palette.Accent, h);
            var edgeWidth = Math.Max(0, _look.FrameThickness) + (outline ? (1.6 + Intensity) * h : 0);
            dc.DrawGeometry(fill, new Pen(Theme.Brush(edgeColor), edgeWidth), geo);

            // Outside labels sit on their own surface pill, so hover fills that pill instead.
            var text = !item.Enabled
                ? palette.TextDim
                : inside ? Blend(palette.Text, Theme.On(palette.Accent), fillAmount > 0.5 ? fillAmount : fillAmount * 0.6) : palette.Text;
            DrawLabel(dc, item, mid, outer, Theme.Brush(text), dpi, span, inside, h, fillAmount);

            for (var k = 0; k < pops; k++) dc.Pop();
        }
    }

    void DrawBadges(DrawingContext dc, Palette palette, double dpi, OpenAnimation anim, double t)
    {
        var n = _items.Count;
        if (n == 0) return;
        var g = _geo;
        var span = g.Span;
        var pen = new Pen(Theme.Brush(palette.WheelEdge), Math.Max(0, _look.FrameThickness));
        var any = AnyHover();

        // A hairline track ties the badges together without filling the middle.
        if (_look.Gap <= 6 && t >= 1) dc.DrawEllipse(null, new Pen(Theme.Brush(palette.RingTrack), 1), g.Center, g.OuterRadius, g.OuterRadius);

        for (var i = 0; i < n; i++)
        {
            var item = _items[i];
            var h = item.Enabled ? Amount(i) : 0;
            var mid = g.Mid(i);
            var at = RadialGeometry.PointAt(g.Center, g.OuterRadius, mid);
            var grow = _look.Hover switch
            {
                HoverEffect.Grow => 0.07 * Intensity * h,
                HoverEffect.Pulse => (0.03 + 0.07 * PulseWave) * Intensity * h,
                _ => 0,
            };
            var r = g.BadgeRadius * (1 + grow);

            var pops = PushSector(dc, anim, t, i, at, mid);
            pops += PushSpotlight(dc, h, any);
            pops += PushLift(dc, mid, h);

            var fillAmount = FillAt(h);
            Brush fill = !item.Enabled
                ? Theme.Brush(Blend(palette.WheelFill, palette.Surface, 0.6))
                : Theme.Brush(Blend(palette.WheelFill, palette.Accent, fillAmount));
            var shape = BadgeGeometry(g.Badge, at, r);
            if (_look.Hover == HoverEffect.Glow && h > 0.01) DrawGlow(dc, shape, palette.Accent, h);
            var edge = h > 0.01
                ? new Pen(Theme.Brush(Blend(palette.WheelEdge, palette.Accent, h)),
                    Math.Max(0, _look.FrameThickness) + (_look.Hover == HoverEffect.Outline ? (1.6 + Intensity) * h : 0))
                : pen;
            dc.DrawGeometry(fill, edge, shape);

            var text = !item.Enabled ? palette.TextDim : Blend(palette.Text, Theme.On(palette.Accent), fillAmount > 0.5 ? fillAmount : fillAmount * 0.6);
            DrawLabel(dc, item, mid, g.OuterRadius, Theme.Brush(text), dpi, span, true, h, fillAmount);

            for (var k = 0; k < pops; k++) dc.Pop();
        }
    }

    static readonly (double Width, double Alpha)[] GlowRings = { (14.0, 0.10), (8.0, 0.16), (4.0, 0.26) };

    /// <summary>A soft accent halo: three widening strokes of the same outline, fading out.</summary>
    static void DrawGlow(DrawingContext dc, Geometry geo, Color accent, double h)
    {
        foreach (var (width, alpha) in GlowRings)
        {
            var pen = new Pen(Theme.Brush(WithAlpha(accent, alpha * h)), width) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
    }

    /// <summary>The outline of one floating badge.</summary>
    static Geometry BadgeGeometry(BadgeShape shape, Point c, double r)
    {
        Geometry geo;
        switch (shape)
        {
            case BadgeShape.Hexagon:
                // Pointy-top, sized so it reads as large as the circle it replaces.
                geo = Polygon(c, r * 1.1, 6);
                break;
            case BadgeShape.Diamond:
                geo = Polygon(c, r * 1.22, 4);
                break;
            case BadgeShape.Tile:
            {
                var side = r * 1.86;
                geo = new RectangleGeometry(new Rect(c.X - side / 2, c.Y - side / 2, side, side), r * 0.42, r * 0.42);
                break;
            }
            default:
                geo = new EllipseGeometry(c, r, r);
                break;
        }
        geo.Freeze();
        return geo;
    }

    /// <summary>A regular polygon with one corner pointing up.</summary>
    static Geometry Polygon(Point c, double radius, int corners)
    {
        var fig = new PathFigure { StartPoint = RadialGeometry.PointAt(c, radius, 0), IsClosed = true, IsFilled = true };
        for (var k = 1; k < corners; k++)
            fig.Segments.Add(new LineSegment(RadialGeometry.PointAt(c, radius, k * 360.0 / corners), true) { IsSmoothJoin = true });
        return new PathGeometry(new[] { fig });
    }

    /// <summary>
    /// Draws one sector's content as a single block. Outside labels get a small surface pill:
    /// the ring floats over an unknown desktop, so bare text would vanish on some wallpapers.
    /// The pill is placed by its own half-extent, so it clears the rim at every angle.
    /// </summary>
    void DrawLabel(DrawingContext dc, ActionDef item, double angle, double outer, Brush brush, double dpi, double span, bool inside, double hover, double fillAmount)
    {
        // Inside a filled wheel the text sits in the band between hub and rim; in a badge layout
        // it sits in the badge itself. Both need a tighter measure than an outside pill: the
        // wedge and the badge are the only room the text is allowed to take.
        var badges = _geo.IsBadges;
        var at = _geo.Center;
        if (inside)
        {
            var rad = (angle - 90) * Math.PI / 180;
            var radius = badges
                ? _geo.OuterRadius
                : (_geo.InnerRadius + _geo.OuterRadius) / 2;
            at = new Point(_geo.Center.X + Math.Cos(rad) * radius, _geo.Center.Y + Math.Sin(rad) * radius);
        }

        var arcRoom = 2 * Math.PI * (badges ? _geo.BadgeRadius : Math.Max(60, _geo.OuterRadius)) * span / 360 - 10;
        var insideRoom = badges ? Math.Max(40, _geo.BadgeRadius * 1.75) : 92;
        var maxWidth = Math.Max(38, Math.Min(inside ? insideRoom : 132, arcRoom));

        // A badge is narrower than a wedge, so the format name is set smaller there: a four-letter
        // name like WebP has to fit inside a circle without turning into an ellipsis.
        var formatSize = (inside && badges ? 13.5 : item.Label.Length > 5 ? 14 : 17) * Scale;

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

        var pushes = 0;
        var magnify = MagnifyAt(hover);
        if (magnify > 1.001)
        {
            dc.PushTransform(new ScaleTransform(magnify, magnify, at.X, at.Y));
            pushes++;
        }
        if (!inside && _look.RotatedLabels)
        {
            // Readable at every position: no label is ever drawn upside down.
            var turn = angle > 90 && angle < 270 ? angle + 180 : angle;
            dc.PushTransform(new RotateTransform(turn, at.X, at.Y));
            pushes++;
        }

        if (!inside)
        {
            var box = new Rect(at.X - width / 2 - padX, at.Y - height / 2 - padY, width + padX * 2, height + padY * 2);
            var palette = Theme.Current;
            var pill = Theme.Brush(Blend(palette.Surface, palette.Accent, fillAmount));
            var pillEdge = Theme.Brush(Blend(palette.WheelEdge, palette.Accent, hover));
            dc.DrawRoundedRectangle(pill, new Pen(pillEdge, 1), box, 7, 7);
            if (fillAmount > 0.5)
            {
                // Over an accent pill the text switches to the accent's own foreground.
                var on = Theme.Brush(Theme.On(palette.Accent));
                label.SetForegroundBrush(on);
                glyph?.SetForegroundBrush(on);
                brush = on;
            }
        }

        var top = at.Y - height / 2;
        if (glyph is not null)
        {
            Put(dc, glyph, at.X, top);
            Put(dc, label, at.X, top + glyph.Height + 2);
        }
        else Put(dc, label, at.X, top);
        if (more) DrawMore(dc, at.X, top + contentHeight + 5, brush);

        for (var i = 0; i < pushes; i++) dc.Pop();
    }

    // Measuring the same few labels on every hover frame is the most expensive part of a redraw;
    // a label's natural width never changes for a given font, size and DPI, so it is kept.
    static readonly Dictionary<(string, double, FontWeight, FontFamily, double), double> NaturalWidths = new();

    /// <summary>Width the text needs when it is not constrained by the sector's wrapping box.</summary>
    static double Natural(string text, double size, FontWeight weight, FontFamily family, double dpi)
    {
        var key = (text, size, weight, family, dpi);
        if (NaturalWidths.TryGetValue(key, out var cached)) return cached;
        var free = new FormattedText(text, CultureInfo.CurrentUICulture, Loc.Flow,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.Transparent, dpi)
        {
            MaxTextWidth = 4000,
        };
        if (NaturalWidths.Count > 2048) NaturalWidths.Clear();
        return NaturalWidths[key] = free.WidthIncludingTrailingWhitespace;
    }

    bool ShowsName(ActionDef item) => _look.Labels switch
    {
        LabelStyle.Icons => false,
        LabelStyle.Text or LabelStyle.Labels => true,
        // Auto: names outside the ring, icons alone when the sector is too narrow to read.
        _ => item.Style != ActionStyle.Format && !_geo.IsBadges || item.Style == ActionStyle.Format,
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
        var active = back ? _hubAmount : 0;
        dc.DrawEllipse(Theme.Brush(Blend(palette.WheelFill, palette.Accent, active)),
            new Pen(Theme.Brush(Blend(palette.WheelEdge, palette.Accent, active)), 1.2), g.Center, g.HubRadius, g.HubRadius);
        var title = Theme.Brush(active > 0.5 ? Theme.On(palette.Accent) : palette.Text);

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
                size => Natural(_title, size, FontWeights.Bold, Theme.DisplayFont, dpi) <= width, 12.0);
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

    static Color Blend(Color a, Color b, double t)
    {
        t = Motion.Clamp01(t);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(Motion.Clamp01(alpha) * c.A), c.R, c.G, c.B);

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

    static Geometry Wedge(double fromDeg, double toDeg, double inner, double outer, double gapPx, MenuGeometry g)
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

    /// <summary>
    /// A rounded petal filling one sector: narrow at the hub, widest through the band and round
    /// at the tip. Two mirrored cubic curves keep the outline smooth at any sector count.
    /// </summary>
    static Geometry Petal(double mid, double span, double inner, double outer, double gapPx, MenuGeometry g)
    {
        var length = outer - inner;
        var widest = inner + length * 0.58;
        // Half the sector's angle at the widest point, less the gap, so neighbours never touch.
        var half = Math.Max(4, Math.Min(span, 120) / 2 - gapPx / 2 / widest * 180 / Math.PI);
        var halfAtRim = Math.Max(3, half * 0.62);
        Point P(double r, double offset) => RadialGeometry.PointAt(g.Center, r, mid + offset);

        var root = P(inner, 0);
        var tip = P(outer, 0);
        var fig = new PathFigure { StartPoint = root, IsClosed = true };
        fig.Segments.Add(new BezierSegment(P(inner + length * 0.18, -half * 1.05), P(outer, -halfAtRim), tip, true));
        fig.Segments.Add(new BezierSegment(P(outer, halfAtRim), P(inner + length * 0.18, half * 1.05), root, true));
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

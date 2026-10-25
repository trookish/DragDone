namespace DragDone.Core;

/// <summary>How the ring arrives when it opens, and when it changes to another ring.</summary>
public enum OpenAnimation
{
    /// <summary>The ring is simply there.</summary>
    None,

    /// <summary>Fades in where it stands.</summary>
    Fade,

    /// <summary>Grows from slightly smaller with a soft overshoot.</summary>
    Pop,

    /// <summary>Grows out of the cursor.</summary>
    Zoom,

    /// <summary>Turns into place while it grows.</summary>
    Spin,

    /// <summary>Sectors appear one after another, clockwise.</summary>
    Cascade,

    /// <summary>Every sector slides out of the hub.</summary>
    Bloom,

    /// <summary>Drops in from above and settles.</summary>
    Drop,

    /// <summary>Springs open with an elastic bounce.</summary>
    Elastic,

    /// <summary>A wave of sectors pops out around the ring.</summary>
    Ripple,
}

/// <summary>What a sector does while a drag rests on it.</summary>
public enum HoverEffect
{
    /// <summary>The sector takes the accent colour and nothing moves.</summary>
    Highlight,

    /// <summary>The sector grows outward.</summary>
    Grow,

    /// <summary>The sector lifts away from the hub.</summary>
    Lift,

    /// <summary>A soft accent glow surrounds the sector.</summary>
    Glow,

    /// <summary>A strong accent outline, the fill stays calm.</summary>
    Outline,

    /// <summary>A gentle accent tint instead of a full fill.</summary>
    Tint,

    /// <summary>The sector breathes while it is held.</summary>
    Pulse,

    /// <summary>The sector's label and icon get larger.</summary>
    Magnify,

    /// <summary>The other sectors dim so the chosen one stands out.</summary>
    Spotlight,
}

/// <summary>
/// The arithmetic behind every animation: easing curves and per-sector timing. Pure, so the
/// curves are unit-tested and the renderer only has to ask "how far along is sector i".
/// </summary>
public static class Motion
{
    public static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t;

    public static double CubicOut(double t)
    {
        t = Clamp01(t);
        var u = 1 - t;
        return 1 - u * u * u;
    }

    public static double QuadInOut(double t)
    {
        t = Clamp01(t);
        return t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
    }

    /// <summary>Overshoots a little past 1 before settling; <paramref name="overshoot"/> 1.70 is the classic back curve.</summary>
    public static double BackOut(double t, double overshoot = 1.70158)
    {
        t = Clamp01(t);
        var c3 = overshoot + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + overshoot * Math.Pow(t - 1, 2);
    }

    /// <summary>A damped spring: rises past 1 and rings down to it.</summary>
    public static double ElasticOut(double t)
    {
        t = Clamp01(t);
        if (t is 0 or 1) return t;
        const double c4 = 2 * Math.PI / 3.2;
        return Math.Pow(2, -9 * t) * Math.Sin((t * 10 - 0.75) * c4) + 1;
    }

    /// <summary>Bounces on arrival, like something dropped onto a surface.</summary>
    public static double BounceOut(double t)
    {
        t = Clamp01(t);
        const double n1 = 7.5625, d1 = 2.75;
        if (t < 1 / d1) return n1 * t * t;
        if (t < 2 / d1) return n1 * (t -= 1.5 / d1) * t + 0.75;
        if (t < 2.5 / d1) return n1 * (t -= 2.25 / d1) * t + 0.9375;
        return n1 * (t -= 2.625 / d1) * t + 0.984375;
    }

    /// <summary>True when the animation moves each sector on its own clock.</summary>
    public static bool IsPerSector(OpenAnimation a) => a is OpenAnimation.Cascade or OpenAnimation.Bloom or OpenAnimation.Ripple;

    /// <summary>
    /// The local progress of sector <paramref name="index"/> of <paramref name="count"/> when the
    /// whole animation is at <paramref name="t"/>. Staggered animations start each sector a
    /// little later than the one before it, and every sector still finishes by t = 1.
    /// </summary>
    public static double SectorProgress(OpenAnimation a, double t, int index, int count)
    {
        t = Clamp01(t);
        if (!IsPerSector(a) || count <= 1) return t;
        var spread = a switch
        {
            OpenAnimation.Cascade => 0.55,
            OpenAnimation.Ripple => 0.45,
            _ => 0.25, // Bloom: almost together, just enough offset to read as organic
        };
        var start = spread * index / (count - 1);
        var length = 1 - spread;
        return Clamp01((t - start) / length);
    }

    /// <summary>The hub leads per-sector animations, so the ring has something to grow out of.</summary>
    public static double HubProgress(OpenAnimation a, double t) =>
        IsPerSector(a) ? Clamp01(t * 2.6) : Clamp01(t);

    /// <summary>
    /// Moves a hover amount toward its target over roughly <paramref name="durationMs"/>, frame
    /// rate independent: the same setting feels the same at 60 Hz and at 144 Hz.
    /// </summary>
    public static double Approach(double current, double target, double dtMs, double durationMs)
    {
        if (durationMs <= 1) return target;
        // Exponential smoothing that covers ~95 % of the distance in the configured time.
        var k = 1 - Math.Exp(-3 * dtMs / durationMs);
        var next = current + (target - current) * k;
        return Math.Abs(target - next) < 0.002 ? target : next;
    }
}

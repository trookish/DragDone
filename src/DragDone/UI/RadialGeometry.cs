using System.Windows;

namespace DragDone.UI;

public enum RadialZone
{
    Hub,
    Ring,
    Outside,
}

/// <summary>
/// Pure geometry of the pie: angle 0 points up, angles grow clockwise (screen space).
/// Item 0 is centred at the top, as in the reference design; the order reads clockwise,
/// which starts on the right-hand side and therefore matches Arabic reading order.
/// </summary>
public static class RadialGeometry
{
    public static double Span(int count) => 360.0 / Math.Max(1, count);

    /// <summary>Angle in degrees of a vector from the centre (0 = up, clockwise).</summary>
    public static double AngleOf(Vector v)
    {
        var deg = Math.Atan2(v.X, -v.Y) * 180 / Math.PI;
        return (deg + 360) % 360;
    }

    public static double CenterAngle(int index, int count) => index * Span(count) % 360;

    public static int IndexAt(double angle, int count)
    {
        var span = Span(count);
        var shifted = (angle + span / 2) % 360;
        return Math.Min(count - 1, (int)Math.Floor(shifted / span));
    }

    public static (RadialZone Zone, int Index) Hit(Point p, Point center, double hubRadius, double outerRadius, int count)
    {
        var v = p - center;
        var r = v.Length;
        if (r <= hubRadius) return (RadialZone.Hub, -1);
        if (r > outerRadius || count == 0) return (RadialZone.Outside, -1);
        return (RadialZone.Ring, IndexAt(AngleOf(v), count));
    }

    public static Point PointAt(Point center, double radius, double angleDeg)
    {
        var a = angleDeg * Math.PI / 180;
        return new Point(center.X + radius * Math.Sin(a), center.Y - radius * Math.Cos(a));
    }
}

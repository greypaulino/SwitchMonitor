namespace SwitchMonitor.Core.Sharing;

/// <summary>Where the other computer sits, seen from this one.</summary>
public enum ShareSide { Right, Left, Above, Below }

/// <summary>The area of all screens of a computer, in pixels (Right and Bottom are exclusive).</summary>
public readonly record struct ShareBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>The rules for passing the mouse from one computer to the other at the edge of the screens.</summary>
public static class ShareEdge
{
    public static ShareSide Opposite(ShareSide side) => side switch
    {
        ShareSide.Right => ShareSide.Left,
        ShareSide.Left => ShareSide.Right,
        ShareSide.Above => ShareSide.Below,
        _ => ShareSide.Above
    };

    /// <summary>The pointer is at the edge toward <paramref name="side"/> and moving toward it (or pushing against it).</summary>
    public static bool Crossed(ShareSide side, int x, int y, int dx, int dy, ShareBounds area) => side switch
    {
        ShareSide.Right => x >= area.Right - 1 && dx > 0,
        ShareSide.Left => x <= area.Left && dx < 0,
        ShareSide.Above => y <= area.Top && dy < 0,
        _ => y >= area.Bottom - 1 && dy > 0
    };

    /// <summary>How far along the edge the pointer is, 0 to 1 (the height for Left and Right, the width for Above and Below).</summary>
    public static double Fraction(ShareSide side, int x, int y, ShareBounds area)
    {
        double value = side is ShareSide.Left or ShareSide.Right
            ? (y - area.Top) / (double)Math.Max(1, area.Height - 1)
            : (x - area.Left) / (double)Math.Max(1, area.Width - 1);
        return Math.Clamp(value, 0, 1);
    }

    /// <summary>
    /// Where the pointer appears on this computer when it comes in from <paramref name="side"/>: just inside that edge, at the
    /// same fraction along it (so it does not leave again at once).
    /// </summary>
    public static (int X, int Y) Landing(ShareSide side, double fraction, ShareBounds area)
    {
        const int inset = 3;
        int along(int start, int size) => start + (int)Math.Round(fraction * Math.Max(0, size - 1));
        return side switch
        {
            ShareSide.Right => (area.Right - 1 - inset, along(area.Top, area.Height)),
            ShareSide.Left => (area.Left + inset, along(area.Top, area.Height)),
            ShareSide.Above => (along(area.Left, area.Width), area.Top + inset),
            _ => (along(area.Left, area.Width), area.Bottom - 1 - inset)
        };
    }
}

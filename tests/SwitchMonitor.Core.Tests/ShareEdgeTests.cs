using SwitchMonitor.Core.Sharing;

namespace SwitchMonitor.Core.Tests;

/// <summary>Passing the mouse to the other computer: when the edge is crossed, and where the pointer lands.</summary>
internal static class ShareEdgeTests
{
    private static readonly ShareBounds Area = new(0, 0, 1920, 1080);

    [Test]
    public static void OnlyPushingAgainstTheConfiguredEdgeCrosses()
    {
        Assert.True(ShareEdge.Crossed(ShareSide.Right, 1919, 500, 4, 0, Area), "right edge, moving right");
        Assert.True(!ShareEdge.Crossed(ShareSide.Right, 1919, 500, -4, 0, Area), "right edge, moving away");
        Assert.True(!ShareEdge.Crossed(ShareSide.Right, 1000, 500, 4, 0, Area), "not at the edge");
        Assert.True(!ShareEdge.Crossed(ShareSide.Left, 1919, 500, 4, 0, Area), "the other side does not cross");
        Assert.True(ShareEdge.Crossed(ShareSide.Left, 0, 500, -1, 0, Area));
        Assert.True(ShareEdge.Crossed(ShareSide.Above, 300, 0, 0, -2, Area));
        Assert.True(ShareEdge.Crossed(ShareSide.Below, 300, 1079, 0, 2, Area));
    }

    [Test]
    public static void ThePointerLandsInsideTheEdgeAtTheSameHeight()
    {
        double fraction = ShareEdge.Fraction(ShareSide.Right, 1919, 540, Area);
        var (x, y) = ShareEdge.Landing(ShareEdge.Opposite(ShareSide.Right), fraction, new ShareBounds(0, 0, 2560, 1440));
        Assert.True(x > 0 && x < 20, "just inside the left edge of the other computer");
        Assert.True(Math.Abs(y - 720) < 3, "about the same relative height");
        Assert.True(!ShareEdge.Crossed(ShareSide.Left, x, y, -0, 0, new ShareBounds(0, 0, 2560, 1440)), "it does not leave again at once");
        var (bx, by) = ShareEdge.Landing(ShareSide.Below, 0.5, Area);
        Assert.True(by > 1000 && bx > 900 && bx < 1000, "coming back from below lands near the bottom edge");
    }

    [Test]
    public static void SidesHaveOpposites()
    {
        Assert.Equal(ShareSide.Left, ShareEdge.Opposite(ShareSide.Right));
        Assert.Equal(ShareSide.Below, ShareEdge.Opposite(ShareSide.Above));
    }
}

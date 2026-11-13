using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Sharing;

/// <summary>One screen of a computer, in that computer's own pixels (Right and Bottom are exclusive).</summary>
public readonly record struct ShareRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    public ShareRect Moved(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    /// <summary>The screen's name on the map: its place and size in its own computer, which do not change when another screen comes or goes.</summary>
    public string Key => $"{Left},{Top},{Width}x{Height}";

    /// <summary>The point of this screen nearest to (x, y).</summary>
    public (int X, int Y) Nearest(int x, int y) => (Math.Clamp(x, Left, Right - 1), Math.Clamp(y, Top, Bottom - 1));
}

/// <summary>A screen as it sits on the map: its own pixels, and where the map puts its origin (the map's pixels).</summary>
public readonly record struct ShareScreen(ShareRect Rect, int Ox, int Oy)
{
    /// <summary>Where it is on the map.</summary>
    public ShareRect OnMap => Rect.Moved(Ox, Oy);
}

/// <summary>
/// Where each screen of this computer and of another one sit on the shared mouse's map: one offset per screen (found by the screen's
/// place and size, not by its number, which changes when a screen comes or goes), so a screen can be put anywhere, apart from the others
/// of its own computer. The map does not change how Windows arranged them: it only says where the mouse passes from one computer to the
/// other. The other computer keeps the same map the other way round (<see cref="Swapped"/>); the newest (<see cref="At"/>) wins.
/// </summary>
public sealed record ShareArrangement(IReadOnlyDictionary<string, (int X, int Y)> Mine, IReadOnlyDictionary<string, (int X, int Y)> Theirs, long At = 0)
{
    public static readonly ShareArrangement Empty = new(new Dictionary<string, (int, int)>(), new Dictionary<string, (int, int)>());

    public ShareArrangement Swapped() => new(Theirs, Mine, At);

    public (int X, int Y) MineAt(ShareRect screen) => Mine.TryGetValue(screen.Key, out var o) ? o : (0, 0);
    public (int X, int Y) TheirsAt(ShareRect screen) => Theirs.TryGetValue(screen.Key, out var o) ? o : (0, 0);

    public ShareArrangement With(bool mine, ShareRect screen, (int X, int Y) offset, long at)
    {
        var m = new Dictionary<string, (int X, int Y)>(Mine);
        var t = new Dictionary<string, (int X, int Y)>(Theirs);
        (mine ? m : t)[screen.Key] = offset;
        return new ShareArrangement(m, t, at);
    }

    /// <summary>This arrangement with every screen of both lists given a place (the ones it lacks get the place <paramref name="fallback"/> has).</summary>
    public ShareArrangement Completed(IReadOnlyList<ShareRect> mine, IReadOnlyList<ShareRect> theirs, ShareArrangement fallback)
    {
        var m = new Dictionary<string, (int X, int Y)>(Mine);
        var t = new Dictionary<string, (int X, int Y)>(Theirs);
        foreach (var s in mine) if (!m.ContainsKey(s.Key)) m[s.Key] = fallback.MineAt(s);
        foreach (var s in theirs) if (!t.ContainsKey(s.Key)) t[s.Key] = fallback.TheirsAt(s);
        return new ShareArrangement(m, t, At);
    }
}

public static class ShareMap
{
    /// <summary>Inside the landing screen, so the pointer does not leave again at once.</summary>
    public const int Inset = 3;

    public static ShareRect Union(IReadOnlyList<ShareRect> screens) => screens.Count == 0
        ? new ShareRect(0, 0, 0, 0)
        : new ShareRect(screens.Min(s => s.Left), screens.Min(s => s.Top), screens.Max(s => s.Right), screens.Max(s => s.Bottom));

    public static ShareScreen[] Place(IReadOnlyList<ShareRect> rects, Func<ShareRect, (int X, int Y)> offsetOf) =>
        rects.Select(r => { var o = offsetOf(r); return new ShareScreen(r, o.X, o.Y); }).ToArray();

    /// <summary>The point of this computer's screens nearest to (x, y): where the pointer is put back when it is somewhere no screen is.</summary>
    public static (int X, int Y) Confine(IReadOnlyList<ShareRect> screens, int x, int y)
    {
        if (screens.Count == 0 || screens.Any(s => s.Contains(x, y))) return (x, y);
        (int X, int Y) best = (x, y);
        long bestDistance = long.MaxValue;
        foreach (var s in screens)
        {
            var p = s.Nearest(x, y);
            long d = (long)(p.X - x) * (p.X - x) + (long)(p.Y - y) * (p.Y - y);
            if (d < bestDistance) { bestDistance = d; best = p; }
        }
        return best;
    }

    /// <summary>
    /// The pointer is at (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="mine"/> (or just off them: the nearest screen
    /// counts) and was moving by (dx, dy). When it pushes out of this computer's screens at a place where, on the map, a screen of
    /// <paramref name="theirs"/> is, this is where it appears on that screen (in that screen's own pixels), with its index.
    /// </summary>
    public static (int X, int Y, int Screen)? Cross(IReadOnlyList<ShareScreen> mine, IReadOnlyList<ShareScreen> theirs, int x, int y, int dx, int dy)
    {
        if (mine.Count == 0 || theirs.Count == 0) return null;
        int here = -1;
        for (int i = 0; i < mine.Count && here < 0; i++) if (mine[i].Rect.Contains(x, y)) here = i;
        if (here < 0)
        {
            // Not on a screen of this computer (a display that shows another computer is not one, yet the pointer can be on it):
            // from the nearest one.
            long best = long.MaxValue;
            for (int i = 0; i < mine.Count; i++)
            {
                var p = mine[i].Rect.Nearest(x, y);
                long d = (long)(p.X - x) * (p.X - x) + (long)(p.Y - y) * (p.Y - y);
                if (d < best) { best = d; here = i; }
            }
            (x, y) = mine[here].Rect.Nearest(x, y);
        }
        var from = mine[here];
        var probes = new List<(int X, int Y)>(2);
        (int, int)? across = dx != 0 ? (x + Math.Sign(dx), y) : null, down = dy != 0 ? (x, y + Math.Sign(dy)) : null;
        foreach (var probe in Math.Abs(dx) >= Math.Abs(dy) ? new[] { across, down } : new[] { down, across })
            if (probe is { } p) probes.Add(p);
        foreach (var (px, py) in probes)
        {
            if (mine.Any(s => s.Rect.Contains(px, py))) continue;          // Windows lets the pointer walk on to another screen of this computer
            int wx = px + from.Ox, wy = py + from.Oy;                      // the probe on the map
            if (mine.Any(s => s.OnMap.Contains(wx, wy))) continue;         // still on this computer's own screens there: not a crossing
            for (int j = 0; j < theirs.Count; j++)
            {
                var s = theirs[j];
                if (!s.OnMap.Contains(wx, wy)) continue;
                int tx = wx - s.Ox, ty = wy - s.Oy;
                return (Math.Clamp(tx, s.Rect.Left + Inset, Math.Max(s.Rect.Left + Inset, s.Rect.Right - 1 - Inset)),
                        Math.Clamp(ty, s.Rect.Top + Inset, Math.Max(s.Rect.Top + Inset, s.Rect.Bottom - 1 - Inset)), j);
            }
        }
        return null;
    }

    /// <summary>
    /// Where the other computer sits when nobody arranged it: all its screens together to the right of this one's for the computer with
    /// the smaller id, to the left for the other (so the two computers agree without talking), tops aligned.
    /// </summary>
    public static ShareArrangement Default(IReadOnlyList<ShareRect> mine, IReadOnlyList<ShareRect> theirs, string myId, string theirId)
    {
        var a = Union(mine); var b = Union(theirs);
        var offset = string.CompareOrdinal(myId, theirId) <= 0 ? (a.Right - b.Left, a.Top - b.Top) : (a.Left - b.Right, a.Top - b.Top);
        return new ShareArrangement(new Dictionary<string, (int, int)>(), theirs.ToDictionary(s => s.Key, _ => offset));
    }

    // ---- Saved arrangement: {"deviceId":{"m":{"key":[x,y]},"t":{"key":[x,y]},"at":ms}, ...} in settings.json ----

    public static Dictionary<string, ShareArrangement> ParseLayout(string? json)
    {
        var result = new Dictionary<string, ShareArrangement>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return result;
            foreach (var (id, node) in root)
                if (node is JsonObject entry && id.Length is > 0 and <= 64)
                    result[id] = ParseArrangement(entry);
        }
        catch (System.Text.Json.JsonException) { }
        return result;
    }

    public static ShareArrangement ParseArrangement(JsonNode? node) =>
        new(Offsets(node?["m"]), Offsets(node?["t"]), node?["at"] is JsonValue v && v.TryGetValue<long>(out long at) ? at : 0);

    public static Dictionary<string, (int X, int Y)> Offsets(JsonNode? node)
    {
        var result = new Dictionary<string, (int X, int Y)>();
        if (node is not JsonObject list) return result;
        foreach (var (key, item) in list.Take(32))
            if (key.Length <= 40 && item is JsonArray { Count: 2 } pair && pair[0] is JsonValue a && pair[1] is JsonValue b && a.TryGetValue<int>(out int x) && b.TryGetValue<int>(out int y))
                result[key] = (Math.Clamp(x, -200_000, 200_000), Math.Clamp(y, -200_000, 200_000));
        return result;
    }

    public static JsonObject ToJson(IReadOnlyDictionary<string, (int X, int Y)> offsets)
    {
        var list = new JsonObject();
        foreach (var (key, o) in offsets) list[key] = new JsonArray(o.X, o.Y);
        return list;
    }

    public static JsonObject ToJson(ShareArrangement a) => new() { ["m"] = ToJson(a.Mine), ["t"] = ToJson(a.Theirs), ["at"] = a.At };

    public static string ToJson(IReadOnlyDictionary<string, ShareArrangement> layout)
    {
        var root = new JsonObject();
        foreach (var (id, a) in layout) root[id] = ToJson(a);
        return root.ToJsonString();
    }

    // ---- Dragging one screen on the map ----

    /// <summary>
    /// The offset asked for while dragging <paramref name="moving"/> (a screen, in its own pixels) made valid: never over one of
    /// <paramref name="fixedScreens"/> (pushed out the shortest way), and, so the pointer can cross, pulled to touch the nearest screen
    /// when within <paramref name="snap"/> pixels of it. Along the touching edge the screen stays exactly where it was dropped.
    /// </summary>
    public static (int X, int Y) Snap(IReadOnlyList<ShareRect> fixedScreens, ShareRect moving, int ox, int oy, int snap)
    {
        if (fixedScreens.Count == 0) return (ox, oy);
        for (int round = 0; round < 6; round++)
        {
            bool moved = false;
            foreach (var m in fixedScreens)
            {
                var t = moving.Moved(ox, oy);
                if (!Overlaps(t, m)) continue;
                int left = m.Right - t.Left, right = t.Right - m.Left, down = m.Bottom - t.Top, up = t.Bottom - m.Top;
                int least = Math.Min(Math.Min(left, right), Math.Min(down, up));
                if (least == left) ox += left; else if (least == right) ox -= right; else if (least == down) oy += down; else oy -= up;
                moved = true;
            }
            if (!moved) break;
        }
        int bestX = 0, bestY = 0, best = snap + 1;
        foreach (var m in fixedScreens)
        {
            var t = moving.Moved(ox, oy);
            if (VerticalSpan(t, m))
            {
                int gap = t.Left >= m.Right ? m.Right - t.Left : t.Right <= m.Left ? m.Left - t.Right : int.MaxValue;
                if (gap != int.MaxValue && Math.Abs(gap) < best) { best = Math.Abs(gap); bestX = gap; bestY = 0; }
            }
            if (HorizontalSpan(t, m))
            {
                int gap = t.Top >= m.Bottom ? m.Bottom - t.Top : t.Bottom <= m.Top ? m.Top - t.Bottom : int.MaxValue;
                if (gap != int.MaxValue && Math.Abs(gap) < best) { best = Math.Abs(gap); bestX = 0; bestY = gap; }
            }
        }
        return (ox + bestX, oy + bestY);
    }

    private static bool Overlaps(ShareRect a, ShareRect b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
    private static bool VerticalSpan(ShareRect a, ShareRect b) => a.Top < b.Bottom && b.Top < a.Bottom;
    private static bool HorizontalSpan(ShareRect a, ShareRect b) => a.Left < b.Right && b.Left < a.Right;
}

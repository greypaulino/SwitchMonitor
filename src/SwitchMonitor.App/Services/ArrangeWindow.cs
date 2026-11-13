using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SwitchMonitor.Core.Sharing;

namespace SwitchMonitor.App.Services;

/// <summary>
/// The map of the shared mouse: boxes for this computer's screens and for another computer's, like Windows' own arrangement. Drag any
/// screen, each one apart from the others, to where it really is on your desk: the pointer then crosses at exactly those edges. It never
/// changes Windows' display settings, only where the shared mouse and keyboard pass from one computer to the other.
/// </summary>
internal sealed class ArrangeWindow : PairWindow
{
    private const double W = 760, H = 340;

    private readonly InputShareService share;
    private readonly Func<string, string?> nameOf;
    private readonly Canvas canvas = new()
    {
        Width = W, Height = H, ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), Focusable = true
    };
    private readonly TextBlock empty = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 13, TextAlignment = TextAlignment.Center };
    private readonly ComboBox chooser = new() { MinWidth = 220, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private double scale = 0.1, originX, originY;
    private string? peerId;                         // the computer this map is for
    private ShareArrangement? working;              // the arrangement being dragged (null when not dragging)
    private (bool Mine, int Index)? dragging;
    private Point dragFrom;
    private (int X, int Y) dragStartOffset;
    private bool filling;
    private static ArrangeWindow? current;

    /// <summary>Opens the map (or brings the open one forward). <paramref name="nameOf"/>: a connected computer's name by device id, else null.</summary>
    public static void Open(InputShareService share, Func<string, string?> nameOf)
    {
        if (current is null)
        {
            current = new ArrangeWindow(share, nameOf);
            current.Closed += (_, _) => current = null;
            current.Show();
        }
        current.Activate();
    }

    private ArrangeWindow(InputShareService share, Func<string, string?> nameOf) : base("Arrange screens for the shared mouse")
    {
        this.share = share;
        this.nameOf = nameOf;
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        panel.Children.Add(new TextBlock { Text = "Where are your screens?", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        panel.Children.Add(new TextBlock
        {
            Text = "Drag each screen to where it really is. The shared mouse crosses at exactly those edges. This does not change Windows' display settings.",
            FontSize = 13, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = W, Margin = new Thickness(0, 6, 0, 14)
        });
        chooser.SelectionChanged += (_, _) => { if (!filling && chooser.SelectedItem is ComboBoxItem { Tag: string id }) { peerId = id; Redraw(); } };
        panel.Children.Add(chooser);
        var frame = new Border { Child = canvas, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("LineBrush"), ClipToBounds = true };
        panel.Children.Add(frame);
        empty.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(empty);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var reset = new Button { Content = "Side by side again", MinWidth = 140, Margin = new Thickness(0, 0, 10, 0) };
        reset.Click += (_, _) => { if (peerId is { } id) { share.ResetArrangement(id); Redraw(); } };
        buttons.Children.Add(reset);
        var done = new Button { Content = "Done", IsDefault = true, MinWidth = 110 };
        done.Click += (_, _) => Close();   // IsCancel only closes a window shown as a dialog
        buttons.Children.Add(done);
        panel.Children.Add(buttons);
        Content = panel;

        canvas.MouseMove += OnMove;
        canvas.MouseLeftButtonUp += OnUp;
        Action changed = () => Dispatcher.BeginInvoke(() => { if (dragging is null) Redraw(); });
        share.MapChanged += changed;
        Closed += (_, _) => share.MapChanged -= changed;
        Loaded += (_, _) => Redraw();
    }

    private void FillChooser(List<(string Id, string Name)> peers)
    {
        // Only rebuilt when the list of computers changed (a redraw while dragging must not reset it).
        var ids = peers.Select(p => p.Id).ToArray();
        var shown = chooser.Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag).ToArray();
        chooser.Visibility = peers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (ids.SequenceEqual(shown)) return;
        filling = true;
        chooser.Items.Clear();
        foreach (var (id, name) in peers) chooser.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        chooser.SelectedIndex = Math.Max(0, ids.ToList().IndexOf(peerId ?? ""));
        filling = false;
    }

    private void Redraw()
    {
        canvas.Children.Clear();
        var peers = share.PeerScreens.Where(p => nameOf(p.Key) is not null).Select(p => (Id: p.Key, Name: nameOf(p.Key)!)).ToList();
        FillChooser(peers);
        if (peerId is null || peers.All(p => p.Id != peerId)) peerId = peers.FirstOrDefault().Id;
        empty.Text = peers.Count == 0 ? "No other computer of your group is connected right now: connect one and its screens appear here." : "";
        if (peerId is not { } id || !share.PeerScreens.TryGetValue(id, out var theirs)) return;

        var mine = share.MyScreens;
        var layout = working ?? share.ArrangementFor(id);
        var placedMine = ShareMap.Place(mine, layout.MineAt);
        var placedTheirs = ShareMap.Place(theirs, layout.TheirsAt);

        if (dragging is null)
        {
            // Fit everything with room around it to drag into.
            var box = ShareMap.Union([.. placedMine.Select(s => s.OnMap), .. placedTheirs.Select(s => s.OnMap)]);
            double bw = Math.Max(1, box.Width) * 1.5, bh = Math.Max(1, box.Height) * 1.7;
            scale = Math.Min(W / bw, H / bh);
            originX = W / 2 - (box.Left + box.Width / 2.0) * scale;
            originY = H / 2 - (box.Top + box.Height / 2.0) * scale;
        }

        var gray = Color.FromRgb(0x44, 0x44, 0x44);
        string myName = Environment.MachineName;
        for (int i = 0; i < placedMine.Length; i++) Tile(placedMine[i], i, true, myName, gray);
        for (int j = 0; j < placedTheirs.Length; j++) Tile(placedTheirs[j], j, false, nameOf(id) ?? "", Color.FromRgb(0x00, 0x78, 0xD4));
    }

    private void Tile(ShareScreen screen, int index, bool mine, string name, Color fill)
    {
        var r = screen.OnMap;
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        label.Children.Add(new TextBlock { Text = (index + 1).ToString(), FontSize = 22, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        label.Children.Add(new TextBlock
        {
            Text = name, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(30, r.Width * scale - 8), HorizontalAlignment = HorizontalAlignment.Center
        });
        var tile = new Border
        {
            Width = Math.Max(8, r.Width * scale - 2), Height = Math.Max(8, r.Height * scale - 2), CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(fill), Child = label, ToolTip = $"{name} · {screen.Rect.Width}×{screen.Rect.Height}",
            Cursor = Cursors.SizeAll, Tag = (mine, index)
        };
        Canvas.SetLeft(tile, originX + r.Left * scale + 1);
        Canvas.SetTop(tile, originY + r.Top * scale + 1);
        tile.MouseLeftButtonDown += OnDown;
        canvas.Children.Add(tile);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ValueTuple<bool, int> tag } || peerId is not { } id) return;
        var layout = share.ArrangementFor(id);
        working = layout;
        dragging = (tag.Item1, tag.Item2);
        dragFrom = e.GetPosition(canvas);
        var minePlaced = share.MyScreens;
        if (!share.PeerScreens.TryGetValue(id, out var theirsPlaced)) return;
        if (tag.Item2 >= (tag.Item1 ? minePlaced.Count : theirsPlaced.Length)) return;
        dragStartOffset = tag.Item1 ? layout.MineAt(minePlaced[tag.Item2]) : layout.TheirsAt(theirsPlaced[tag.Item2]);
        canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (dragging is not { } drag || peerId is not { } id || e.LeftButton != MouseButtonState.Pressed) return;
        if (!share.PeerScreens.TryGetValue(id, out var theirs)) return;
        var at = e.GetPosition(canvas);
        int x = dragStartOffset.X + (int)Math.Round((at.X - dragFrom.X) / scale), y = dragStartOffset.Y + (int)Math.Round((at.Y - dragFrom.Y) / scale);
        var start = share.ArrangementFor(id);
        var mine = share.MyScreens;
        if (drag.Index >= (drag.Mine ? mine.Count : theirs.Length)) return;
        // Every other screen on the map is in the way (or an edge to touch).
        var others = new List<ShareRect>();
        for (int i = 0; i < mine.Count; i++) if (!(drag.Mine && drag.Index == i)) others.Add(mine[i].Moved(start.MineAt(mine[i]).X, start.MineAt(mine[i]).Y));
        for (int j = 0; j < theirs.Length; j++) if (!(!drag.Mine && drag.Index == j)) others.Add(theirs[j].Moved(start.TheirsAt(theirs[j]).X, start.TheirsAt(theirs[j]).Y));
        var moving = drag.Mine ? mine[drag.Index] : theirs[drag.Index];
        var snapped = ShareMap.Snap(others, moving, x, y, (int)Math.Round(24 / scale));
        working = start.With(drag.Mine, moving, snapped, 0);
        Redraw();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (dragging is null || peerId is not { } id) return;
        var result = working;
        dragging = null;
        working = null;
        canvas.ReleaseMouseCapture();
        if (result is not null) share.SetArrangement(id, result);
        Redraw();
    }
}

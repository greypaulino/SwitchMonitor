using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SwitchMonitor.App.ViewModels;

namespace SwitchMonitor.App;

/// <summary>The Apps tab: a program's pill is picked up and follows the pointer until it is dropped on a column.</summary>
public partial class AppsView : UserControl
{
    private Point start;
    private AppChip? pressed;
    private bool dragging;

    public AppsView()
    {
        InitializeComponent();
        // Preview events: they arrive whatever the pill or column under the pointer does with the button.
        PreviewMouseMove += OnMove;
        PreviewMouseLeftButtonUp += OnUp;
        LostMouseCapture += (_, _) => { if (!Mouse.LeftButton.HasFlag(MouseButtonState.Pressed)) return; EndDrag(); };
    }

    /// <summary>The mode selector of a pill: only a choice by hand counts (the list also fires when it is filled in).</summary>
    private void ModePicked(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: AppChip chip } box || DataContext is not AppsViewModel model) return;
        if (!box.IsDropDownOpen && !box.IsKeyboardFocusWithin) return;
        if (box.SelectedValue is string mode) model.SetMode(chip, mode);
    }

    private void ChipDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppChip chip) return;
        // The mode selector inside the pill is used, not dragged.
        for (var at = e.OriginalSource as DependencyObject; at is not null && at != sender; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at is ComboBoxItem or ComboBox or System.Windows.Controls.Primitives.ToggleButton) return;
        pressed = chip;
        start = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (pressed is null) return;
        if (!dragging)
        {
            var now = e.GetPosition(this);
            if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragging = true;
            GhostTint.Background = pressed.Fill;
            GhostName.Text = pressed.Name;
            GhostIcon.Source = pressed.Icon;
            Ghost.Visibility = Visibility.Visible;
            Ghost.UpdateLayout();
            Cursor = Cursors.SizeAll;
        }
        // The pill is held by its middle, so it sits under the pointer.
        var at = e.GetPosition(Root);
        Canvas.SetLeft(Ghost, at.X - Ghost.ActualWidth / 2);
        Canvas.SetTop(Ghost, at.Y - Ghost.ActualHeight / 2);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        var chip = pressed;
        bool wasDragging = dragging;
        Point where = e.GetPosition(this);
        EndDrag();
        if (!wasDragging || chip is null || DataContext is not AppsViewModel model) return;
        if (ColumnAt(where) is { } column) model.Move(chip, column);
    }

    private void EndDrag()
    {
        pressed = null;
        dragging = false;
        Ghost.Visibility = Visibility.Collapsed;
        Cursor = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    /// <summary>The column under a point (the ghost never gets in the way: it is not hit-testable).</summary>
    private AppColumn? ColumnAt(Point point)
    {
        // By the place each column takes (the column the pointer is inside), not by what is hit: it cannot miss.
        foreach (var item in ColumnsList.Items)
        {
            if (ColumnsList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container) continue;
            var inside = TranslatePoint(point, container);
            if (inside.X >= 0 && inside.Y >= 0 && inside.X <= container.ActualWidth && inside.Y <= container.ActualHeight)
                return item as AppColumn;
        }
        return null;
    }
}

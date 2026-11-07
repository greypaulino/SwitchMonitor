using System.Windows;

namespace SwitchMonitor.App;

/// <summary>
/// Shared state for the button look (App.xaml: FlatButton, ItemButtonTemplate). <see cref="IsSelectedProperty"/> marks the
/// current choice (the active profile, input or picture mode): the button fades its "selected" layer in and out with the
/// same ease-in-out as hover and press, instead of changing at once.
/// </summary>
public static class ButtonMotion
{
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.RegisterAttached("IsSelected", typeof(bool), typeof(ButtonMotion), new PropertyMetadata(false));

    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);
}

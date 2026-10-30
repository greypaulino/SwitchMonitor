using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SwitchMonitor.App;

/// <summary>Minimal INotifyPropertyChanged base (no MVVM package: v2 has no third-party dependencies).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute) : this(_ => execute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>False shows the element, true collapses it (the opposite of BooleanToVisibilityConverter).</summary>
public sealed class InverseBoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when every value is "off": false, 0 or null (no displays and not busy → the empty state).</summary>
public sealed class AllFalseToVisibility : System.Windows.Data.IMultiValueConverter
{
    public static AllFalseToVisibility Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        values.All(v => v is null or false or 0) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when every value is "on": true or a number above 0 (a controllable display with controls).</summary>
public sealed class AllTrueToVisibility : System.Windows.Data.IMultiValueConverter
{
    public static AllTrueToVisibility Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        values.All(v => v is true || v is int n && n > 0) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

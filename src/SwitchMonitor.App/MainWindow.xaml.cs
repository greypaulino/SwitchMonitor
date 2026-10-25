using System.Windows;
using System.Windows.Input;
using SwitchMonitor.App.ViewModels;
using SwitchMonitor.Core.Hotkeys;

namespace SwitchMonitor.App;

/// <summary>Main window. Behavior lives in the view models; this only records shortcuts on the Shortcuts tab.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => Record(e);
        Deactivated += (_, _) => (DataContext as MainViewModel)?.ShortcutEditor?.CancelCapture();
    }

    /// <summary>The ⋯ button opens its menu on a normal click.</summary>
    private void ShowOptions(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Record(KeyEventArgs e)
    {
        if (DataContext is not MainViewModel { IsShortcutsTab: true, ShortcutEditor: { IsCapturing: true } editor }) return;
        e.Handled = true;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,          // keys pressed with Alt
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };
        var modifiers = HotkeyModifiers.None;
        var held = Keyboard.Modifiers;
        if (held.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Ctrl;
        if (held.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (held.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (held.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Win;

        if (modifiers == HotkeyModifiers.None && key == Key.Escape)
        {
            editor.CancelCapture();
            return;
        }
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || HotkeyChord.IsModifierKey(virtualKey))
        {
            editor.ShowPending(modifiers);
            return;
        }
        editor.Capture(new HotkeyChord(modifiers, virtualKey));
    }
}

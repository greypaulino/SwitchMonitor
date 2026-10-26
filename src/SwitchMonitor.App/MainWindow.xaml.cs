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
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>Dark caption to match the window (attribute 20; 19 on Windows 10 builds before 20H1). Ignored where unsupported.</summary>
    private void UseDarkTitleBar()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
    }

    /// <summary>The ⋯ and "Add profile" buttons open their menu on a normal click.</summary>
    private void ShowOptions(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;
        menu.DataContext = button.DataContext ?? DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>"Add open app": lists the programs with a window at that moment, then opens the menu.</summary>
    private void ShowRunningApps(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProfileEditorViewModel editor }) editor.RefreshRunningApps();
        ShowOptions(sender, e);
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

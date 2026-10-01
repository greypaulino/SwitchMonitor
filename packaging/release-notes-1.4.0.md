# SwitchMonitor 1.4.0

This release adds brightness control for DDC/CI monitors. A second tray icon opens a panel with one slider per detected monitor; the mouse wheel adjusts the monitor row under the pointer. The chain control links brightness across monitors. The panel can be opened or closed with the tray icon, and its animation reverses from its current position when clicked again.

The default brightness shortcuts are Ctrl+Alt+- and Ctrl+Alt++ to decrease or increase brightness, and Ctrl+Alt+* to select the next monitor. They can be changed in Settings. A tap changes one point; holding a shortcut accelerates to five-point and then ten-point steps.

The installer includes the official AutoHotkey interpreter, application scripts, and ControlMyMonitor. This packaging avoids a Windows Defender detection of the Ahk2Exe-generated binary. Monitor brightness requires a working DDC/CI brightness command. Existing settings are preserved when updating.

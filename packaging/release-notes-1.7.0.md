# SwitchMonitor 1.7.0

Built-in laptop displays now appear in Settings and the brightness panel. They use Windows WMI brightness control and do not show input shortcuts. The LG 29WK600 input list is limited to its physical HDMI 1, HDMI 2, and DisplayPort 1 ports.

The brightness panel now highlights the active display with a rounded background. Hover over a display or press `*` to move the highlight smoothly. While the panel is open, `+` and `-` adjust brightness without Ctrl+Alt. Small and rapid changes follow immediately; larger jumps animate. Linking displays combines them into one slider, with a fade between layouts. Slider and icon repainting was revised to remove flicker and stale visual trails.

Settings can now save the linked-brightness option even when a built-in display with no input rows is selected. The panel also closes when Settings opens or when the taskbar is clicked outside the sun icon.

Existing monitor profiles, shortcuts, and brightness preferences remain in the user data directory during an update.

# SwitchMonitor 1.6.3

The brightness panel marks the active monitor with a green dot and other monitors with gray dots. Linked brightness marks all affected monitors green.

Monitor connections now refresh after Windows display and device changes, with a periodic fallback. The selected monitor stays selected when it remains available.

Settings now has backup and restore buttons for monitor profiles, shortcuts, brightness preferences, and Start with Windows. The About window has a notification test that confirms whether clicking a Windows notification reaches SwitchMonitor, without downloading an update.

The notification click handler now reads the event code correctly for modern tray icon callbacks. The brightness panel repaints its newly exposed dark area during entrance to address the reported Windows 11 white flash. Please test these visual and notification changes on Windows 11.

Existing user settings remain in the user data directory during installation.

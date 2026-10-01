# SwitchMonitor 1.4.1

Fixes a GDI+ initialization error that could prevent the brightness panel from opening from its tray icon immediately after startup. Image rendering now passes the correctly sized startup structure on 64-bit Windows. This release also fixes an abnormal exit during image cleanup.

Existing settings are preserved when updating from 1.4.0.

# SwitchMonitor 1.10.0

The brightness panel can now change the Windows main display with a long press of **M**. On an extended desktop, switching the current main monitor to another computer moves the main display to a remaining screen; when that monitor returns to its original input, SwitchMonitor restores the previous main display. The app does not perform this handoff in single-screen modes.

The main-display control now applies the full Windows display layout in one operation. This fixes a failure seen when changing the main display back from the LG monitor to the Samsung monitor on the tested AMD PC. AMD LG input switching now tries standard VCP 0x60 first, reads back the selected input when possible, and uses the LG command as a fallback. Input-cycle history is saved across restarts, with a return option in the tray menu.

Manual brightness entry now applies one second after the last digit. Its temporary overlay no longer displays a countdown. Existing user settings remain in `%LOCALAPPDATA%\SwitchMonitor` during an update.

# SwitchMonitor 1.6.5

Fixes a Settings crash that could show "Control is destroyed" while monitor input rows were refreshing. Closing Settings now waits for the current refresh to finish, and repeated close events cannot destroy the same window twice.

This is a maintenance update to 1.6.4. Existing monitor profiles, shortcuts, and brightness settings remain in the user data directory.

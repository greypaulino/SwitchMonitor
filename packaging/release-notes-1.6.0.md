# SwitchMonitor 1.6.0

Brightness now uses each monitor's reported VCP 0x10 maximum to map its hardware scale to 0–100%. A monitor with a maximum of 50, for example, displays its raw value of 25 as 50%. Range discovery runs in the background and is cached per monitor.

Linking brightness across multiple monitors now shows their names with one shared slider. Moving it applies the selected percentage to every monitor using each monitor's own hardware range.

The slider thumb uses eased movement. This release also reduces indicator flicker during mouse and keyboard adjustment and applies a dark window background and border during the panel's entrance on Windows 11.

The installer updates existing installations in place and preserves user settings.

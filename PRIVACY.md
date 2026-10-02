# Privacy notice

SwitchMonitor stores monitor choices, keyboard shortcuts, brightness preferences and startup settings on your computer. The WPF preview stores its profile in `%LOCALAPPDATA%\SwitchMonitor-Wpf`; the AutoHotkey edition uses `%LOCALAPPDATA%\SwitchMonitor`. Neither profile is uploaded by the application.

By default, the application checks `api.github.com/repos/greypaulino/SwitchMonitor/releases/latest` for updates at startup, periodically and when you choose **Check for updates**. If you choose to install an update, it downloads release files from GitHub and checks their published SHA-256 hash. These connections disclose ordinary request metadata, such as your IP address, to GitHub under [GitHub's privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement). SwitchMonitor does not send monitor names, brightness values or shortcut settings to GitHub.

In the WPF edition, you can turn off automatic release checks in Settings or deselect **Check GitHub for updates automatically** during installation. A manual check still connects to GitHub when you request it.

Startup shortcuts are created only when you enable **Start with Windows**. You can remove the app using its uninstaller, and delete the local profile folder if you also want to remove saved preferences. The portable WPF preview can be removed by deleting its extracted folder.

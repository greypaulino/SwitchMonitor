# Privacy notice

## SwitchMonitor 2

SwitchMonitor 2 stores its settings, profiles, learned monitor settings, paired phones and logs on your computer in `%LOCALAPPDATA%\SwitchMonitor-v2`. Nothing is uploaded by the application.

The only internet connection is the optional download of the shared monitor database (**Download** in Settings), which reads `raw.githubusercontent.com/greypaulino/SwitchMonitor/main/database/quirks.json` and its SHA-256. That request discloses ordinary metadata, such as your IP address, to GitHub under [GitHub's privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement). Version 2 does not check for application updates on its own.

**Phone access** is off until you turn it on in *Settings → Phone*. While it is on, the computer listens on TCP port 47820 and answers discovery on UDP port 47821, only for devices on private (home) network addresses. Only phones paired with a one-time code can control the displays. The pairing secret is kept encrypted for your Windows account (and with the Android Keystore on the phone) and is not sent again after pairing. The phone receives the display names, brightness, profiles, picture modes, inputs and the Windows volume; it is not sent anything else, and the Android app sends nothing outside your network. Removing a phone in Settings disconnects it immediately.

Startup shortcuts are created only when you enable **Start with Windows**. To remove SwitchMonitor 2, delete its folder and, if you also want to remove its settings, `%LOCALAPPDATA%\SwitchMonitor-v2`. On Android, uninstall the app.

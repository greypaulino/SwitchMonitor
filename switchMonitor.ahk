#Requires AutoHotkey v2.0
#SingleInstance Force
#Include intelLegacyDdc.ahk
#Include amdLgDdc.ahk
#Include returnSyncState.ahk
#Include settingsUi.ahk
#Include brightness.ahk
;@Ahk2Exe-SetName SwitchMonitor
;@Ahk2Exe-SetDescription SwitchMonitor - monitor input shortcuts
;@Ahk2Exe-SetVersion 1.10.1.0
;@Ahk2Exe-SetOrigFilename SwitchMonitor.exe

APP_VERSION := '1.10.1'

monitorTool := FileExist(A_ScriptDir '\ControlMyMonitor\ControlMyMonitor.exe')
    ? A_ScriptDir '\ControlMyMonitor\ControlMyMonitor.exe'
    : (FileExist(A_ScriptDir '\ControlMyMonitor.exe')
        ? A_ScriptDir '\ControlMyMonitor.exe' : A_MyDocuments '\ControlMyMonitor\ControlMyMonitor.exe')
if FileExist(A_ScriptDir '\monitor-switch.ico')
    TraySetIcon(A_ScriptDir '\monitor-switch.ico')
A_IconTip := 'SwitchMonitor'
EnableDarkTheme()
settingsFile := AppPath('switchMonitor.ini')
selectedMonitor := 0
assignments := Map()
monitorProfiles := Map()
profileError := ''
globalShortcuts := Map('cycle', 'Ctrl|Alt|M', 'settings', 'Ctrl|Alt|Shift|M',
    'brightnessDown', 'Ctrl|Alt|NumpadSub', 'brightnessUp', 'Ctrl|Alt|NumpadAdd',
    'brightnessNext', 'Ctrl|Alt|NumpadMult')
registeredGlobalHotkeys := []
busy := false
wizardOpen := false
uiTest := false
capabilitiesFile := AppPath('monitorCapabilities.ini')
availableMonitors := []
brightnessTopologySignature := ''
testCodes := []
registeredBindings := []
buttonStyles := Map()
buttonImages := Map()
gdipToken := 0
buttonHoverHwnd := 0
cycleEntries := []
inputReturnHistory := Map()
inputReturnFile := AppPath('inputReturn.ini')
mainDisplayHandoff := 0
mainDisplayHandoffFile := AppPath('mainDisplayHandoff.ini')
wizardCycle := 0
returnWatches := Map()
returnWatchEnabled := false
brightnessValues := Map()
brightnessRanges := Map()
brightnessRangeJobs := Map()
brightnessPending := Map()
brightnessSelectedKey := ''
brightnessLinked := false
brightnessPanel := 0
brightnessPainting := 0
brightnessLastActivity := 0
brightnessPointerDown := false
brightnessHotkeyActive := false
brightnessCaptureActive := false
sunIconHandle := 0
availableUpdate := {version: '', url: '', hash: ''}
updateNoticeActive := false
notificationTestActive := false
OnMessage(0x404, UpdateNotificationClicked)
OnMessage(0x7E, MonitorTopologyChanged)
OnMessage(0x219, MonitorDeviceChanged)

try {
    if A_Args.Length && A_Args[1] = '--self-test' {
        FileOpen(AppPath('monitor-test-errors.txt'), 'w', 'UTF-8').Close()
        FileOpen(AppPath('monitor-selftest-result.txt'), 'w', 'UTF-8').Close()
        SelfTest()
        ExitApp(0)
    }
    if !FileExist(monitorTool)
        throw Error('ControlMyMonitor was not found: ' monitorTool)
    monitors := ParseMonitors(ExportMonitors())
    if !monitors.Length
        throw Error('ControlMyMonitor detected no monitors.')
    if A_Args.Length && A_Args[1] = '--brightness-check' {
        report := ''
        for monitor in monitors {
            range := BrightnessRange(monitor)
            report .= MonitorLabel(monitor) ': current=' range.current ', maximum=' range.max ', displayed=' BrightnessToPercent(range.current, range.max) '%`n'
        }
        FileAppend(report, AppPath('brightness-check-result.txt'), 'UTF-8')
        ExitApp(0)
    }
    if A_Args.Length && A_Args[1] = '--diagnose' {
        DiagnoseMonitors(monitors)
        ExitApp(0)
    }
    if A_Args.Length && A_Args[1] = '--test-input' {
        if A_Args.Length < 2 || !RegExMatch(A_Args[2], '^\d+$')
            throw Error('Usage: --test-input code [monitor number]')
        if monitors.Length > 1 && A_Args.Length < 3
            throw Error('Multiple monitors detected. Specify the number shown by --diagnose.')
        index := A_Args.Length >= 3 ? Integer(A_Args[3]) : 1
        if index < 1 || index > monitors.Length
            throw Error('Monitor number is outside the list.')
        inputCode := Integer(A_Args[2])
        if inputCode < 1 || inputCode > 65535
            throw Error('Input code is out of range.')
        ok := SendInputCommand(monitors[index], {code: inputCode, name: PortName(inputCode)}, -1, false)
        ExitApp(ok ? 0 : 1)
    }
    if A_Args.Length && A_Args[1] = '--check' {
        report := ''
        for monitor in monitors {
            saved := LoadAssignments(settingsFile, monitor.key)
            detected := DiscoverInputs(monitor, saved)
            report .= monitor.name ' | ' monitor.device ' | asignaciones guardadas: ' saved.Count '`n'
            for row in MakeRows(detected.values, saved, IsLg29wk600(monitor))
                report .= row.name ' | codigo ' row.code ' | ' (ShortcutLabel(row.shortcut)) '`n'
            report .= detected.message '`n'
        }
        FileAppend(report 'PASS: deteccion y lectura de perfiles, sin cambios de entrada.`n', AppPath('monitor-check-result.txt'), 'UTF-8')
        ExitApp(0)
    }
    ; Avoid two copies (installed, portable or source) registering the same keys.
    instanceMutex := DllCall('CreateMutexW', 'Ptr', 0, 'Int', 0,
        'Str', 'Local\SwitchMonitor-A153C1DB-CA20-448F-B320-37E0448E041A', 'Ptr')
    mutexError := A_LastError
    if !instanceMutex
        throw Error('SwitchMonitor could not start. Windows error: ' mutexError)
    if mutexError = 183 {
        DllCall('CloseHandle', 'Ptr', instanceMutex)
        if !A_Args.Length
            MsgBox('SwitchMonitor is already running. Open Settings from the tray icon.', 'SwitchMonitor', 'Iconi')
        ExitApp(0)
    }
    returnWatchEnabled := true
    availableMonitors := monitors
    brightnessTopologySignature := CurrentDisplayTopologySignature()
    LoadInputReturnHistory()
    LoadMainDisplayHandoff()
    LoadGlobalShortcuts()
    brightnessLinked := IniRead(settingsFile, 'Brightness', 'Linked', '0') = '1'
    LoadMonitorProfiles()
    preferred := IniRead(settingsFile, 'General', 'SelectedMonitor', '')
    index := FindMonitorIndex(preferred)
    SelectActiveMonitor(index, false)
    MigrateStartupShortcut()
    BuildTrayMenu()
    try {
        RegisterShortcuts()
        ValidateGlobalShortcuts(monitorProfiles, globalShortcuts)
        RegisterGlobalShortcuts()
    }
    catch as err {
        profileError := err.Message
        OpenLearning()
        MsgBox(err.Message '`n`nEdit the shortcuts in Settings and save again.', 'Shortcut conflict', 'Icon!')
    }
    InitBrightness()
    ShowPendingUpdateCompletion()
    if !wizardOpen && (!A_Args.Length || A_Args[1] != '--activate' || !monitorProfiles.Count)
        OpenLearning()
    SetTimer(CheckForUpdatesSilent, -5000)
    SetTimer(CheckForUpdatesSilent, 21600000)
    SetTimer(PollMonitorTopology, 30000)
    if IsObject(mainDisplayHandoff)
        SetTimer(CheckMainDisplayHandoff, 1500)
} catch as err {
    if A_Args.Length {
        FileAppend('FAIL: ' err.Message '`n' err.Stack '`n', AppPath('monitor-test-errors.txt'), 'UTF-8')
    } else {
        MsgBox(err.Message, 'Monitor', 'Iconx')
    }
    ExitApp(1)
}

FindMonitorIndex(key) {
    global availableMonitors
    for index, monitor in availableMonitors
        if monitor.key = key
            return index
    return 1
}

MonitorLabel(monitor) {
    return IsLg29wk600(monitor) ? 'LG 29WK600'
        : IsInternalDisplay(monitor) ? InternalDisplayLabel(monitor) : monitor.name
}

InternalDisplayLabel(monitor) {
    model := monitor.HasOwnProp('laptopModel') ? monitor.laptopModel : ''
    if RegExMatch(model, 'i)\b([A-Z]+\d{3,})$', &match)
        model := match[1]
    if model = '' && monitor.model != '' && monitor.model != 'N/A'
        model := monitor.model
    return model != '' ? model ' Laptop Screen' : 'Built-in display'
}

LaptopModelName() {
    static cached := false, model := ''
    if cached
        return model
    cached := true
    try {
        service := ComObjGet('winmgmts:\\.\root\cimv2')
        for item in service.ExecQuery('SELECT Model FROM Win32_ComputerSystem') {
            candidate := Trim(item.Model '')
            if candidate != '' && !RegExMatch(candidate,
                'i)^(?:N/?A|Unknown|Default string|System Product Name|To Be Filled By O\.E\.M\.)$')
                model := candidate
            break
        }
    }
    return model
}

LoadMonitorProfiles() {
    global availableMonitors, monitorProfiles, settingsFile
    monitorProfiles := Map()
    for monitor in availableMonitors {
        if IsInternalDisplay(monitor)
            continue
        saved := LoadAssignments(settingsFile, monitor.key)
        if !saved.Count
            continue
        found := DiscoverInputs(monitor, saved)
        rows := MakeRows(found.values, saved, IsLg29wk600(monitor))
        monitorProfiles[monitor.key] := {monitor: monitor, rows: rows, assignments: saved}
    }
}

SelectActiveMonitor(index, persist := true) {
    global availableMonitors, selectedMonitor, assignments, cycleEntries, monitorProfiles, settingsFile
    selectedMonitor := availableMonitors[index]
    if monitorProfiles.Has(selectedMonitor.key) {
        profile := monitorProfiles[selectedMonitor.key]
        assignments := profile.assignments
        cycleEntries := profile.rows
    } else {
        assignments := Map()
        cycleEntries := []
    }
    if persist
        IniWrite(selectedMonitor.key, settingsFile, 'General', 'SelectedMonitor')
    BuildTrayMenu()
}

SelectTrayMonitor(key, *) {
    global availableMonitors, busy, wizardOpen
    if busy || wizardOpen
        return
    for index, monitor in availableMonitors
        if monitor.key = key {
            SelectActiveMonitor(index)
            return
        }
}

BuildTrayMenu() {
    global availableMonitors, selectedMonitor, globalShortcuts, availableUpdate, inputReturnHistory
    A_TrayMenu.Delete()
    if availableMonitors.Length = 1 {
        title := MonitorLabel(availableMonitors[1])
        A_TrayMenu.Add(title, (*) => 0)
        A_TrayMenu.Disable(title)
    } else {
        for index, monitor in availableMonitors {
            title := MonitorLabel(monitor) '  (' index ')'
            A_TrayMenu.Add(title, SelectTrayMonitor.Bind(monitor.key))
            if IsObject(selectedMonitor) && selectedMonitor.key = monitor.key
                A_TrayMenu.Check(title)
        }
    }
    A_TrayMenu.Add()
    A_TrayMenu.Add('Settings', OpenLearning)
    A_TrayMenu.Add('Shortcuts', ShowAssignments)
    A_TrayMenu.Add('Next input (' ShortcutLabel(globalShortcuts['cycle']) ')', NextConnected)
    A_TrayMenu.Add('Return to previous input', ReturnPreviousInput)
    if !IsObject(selectedMonitor) || !inputReturnHistory.Has(selectedMonitor.key)
        A_TrayMenu.Disable('Return to previous input')
    A_TrayMenu.Add('Brightness control', ShowBrightnessPanel)
    if availableUpdate.version != ''
        A_TrayMenu.Add('Update available! Click to install', InstallAvailableUpdate)
    else
        A_TrayMenu.Add('Check for updates', CheckForUpdates)
    A_TrayMenu.Add('About', ShowAbout)
    A_TrayMenu.Add('Start with Windows', ToggleStartWithWindows)
    if StartsWithWindows()
        A_TrayMenu.Check('Start with Windows')
    A_TrayMenu.Add('Exit', (*) => ExitApp())
    A_TrayMenu.Default := 'Settings'
}

StartsWithWindows() {
    path := A_Startup '\SwitchMonitor.lnk'
    if !FileExist(path)
        return false
    try {
        FileGetShortcut(path, &target)
        return FileExist(target) != ''
    }
    return false
}

MigrateStartupShortcut() {
    legacy := RegRead('HKCU\Software\Microsoft\Windows\CurrentVersion\Run', 'SwitchMonitor', '')
    if legacy = ''
        return
    if !StartsWithWindows()
        ConfigureStartup(true)
    else
        RegDelete('HKCU\Software\Microsoft\Windows\CurrentVersion\Run', 'SwitchMonitor')
}

ToggleStartWithWindows(*) {
    try {
        ConfigureStartup(!StartsWithWindows())
        BuildTrayMenu()
    } catch as err {
        MsgBox(err.Message, 'Start with Windows', 'Iconx')
    }
}

ConfigureStartup(enabled, shortcutPath := '') {
    actualStartup := shortcutPath = ''
    path := actualStartup ? A_Startup '\SwitchMonitor.lnk' : shortcutPath
    if enabled {
        target := A_IsCompiled ? A_ScriptFullPath : A_AhkPath
        args := A_IsCompiled ? '--activate' : '"' A_ScriptFullPath '" --activate'
        icon := FileExist(A_ScriptDir '\monitor-switch.ico') ? A_ScriptDir '\monitor-switch.ico' : ''
        FileCreateShortcut(target, path, A_ScriptDir, args, 'SwitchMonitor', icon)
        if !FileExist(path)
            throw Error('The Startup shortcut could not be created.')
    } else if FileExist(path) {
        FileDelete(path)
    }
    if actualStartup
        try RegDelete('HKCU\Software\Microsoft\Windows\CurrentVersion\Run', 'SwitchMonitor')
}

LoadGlobalShortcuts() {
    global settingsFile, globalShortcuts
    for kind, fallback in Map('cycle', 'Ctrl|Alt|M', 'settings', 'Ctrl|Alt|Shift|M',
        'brightnessDown', 'Ctrl|Alt|NumpadSub', 'brightnessUp', 'Ctrl|Alt|NumpadAdd',
        'brightnessNext', 'Ctrl|Alt|NumpadMult') {
        saved := IniRead(settingsFile, 'GlobalShortcuts', kind, fallback)
        try globalShortcuts[kind] := saved = '' ? '' : ValidateChord(saved)
        catch
            globalShortcuts[kind] := fallback
    }
}

AhkChord(chord) {
    result := ''
    for token in StrSplit(chord, '|') {
        if token = 'Ctrl'
            result .= '^'
        else if token = 'Alt'
            result .= '!'
        else if token = 'Shift'
            result .= '+'
        else if token = 'Win'
            result .= '#'
        else
            result .= token
    }
    return result
}

RegisterGlobalShortcuts() {
    global globalShortcuts, registeredGlobalHotkeys
    for binding in registeredGlobalHotkeys
        Hotkey(binding, 'Off')
    registeredGlobalHotkeys := []
    for kind, action in Map('cycle', NextConnected, 'settings', OpenLearning,
        'brightnessDown', BrightnessDown, 'brightnessUp', BrightnessUp,
        'brightnessNext', NextBrightnessMonitor) {
        if globalShortcuts[kind] = ''
            continue
        name := AhkChord(globalShortcuts[kind])
        Hotkey(name, action, 'On')
        registeredGlobalHotkeys.Push(name)
        if kind = 'brightnessDown' && globalShortcuts[kind] = 'Ctrl|Alt|NumpadSub' {
            Hotkey('^!-', BrightnessDown, 'On')
            registeredGlobalHotkeys.Push('^!-')
        } else if kind = 'brightnessUp' && globalShortcuts[kind] = 'Ctrl|Alt|NumpadAdd' {
            Hotkey('^!+=', BrightnessUp, 'On')
            registeredGlobalHotkeys.Push('^!+=')
        } else if kind = 'brightnessNext' && globalShortcuts[kind] = 'Ctrl|Alt|NumpadMult' {
            Hotkey('^!+8', NextBrightnessMonitor, 'On')
            registeredGlobalHotkeys.Push('^!+8')
        }
    }
}

ValidateGlobalShortcuts(profiles, shortcuts) {
    seen := Map()
    for kind, chord in shortcuts {
        if chord = ''
            continue
        for name in GlobalHotkeyNames(kind, chord) {
            if seen.Has(name)
                throw Error(ShortcutLabel(chord) ' conflicts with another global shortcut.')
            seen[name] := kind
        }
    }
    for key, profile in profiles
        for slot, entry in profile.assignments
            for kind, chord in shortcuts
                if chord != '' && HasCode(GlobalHotkeyNames(kind, chord), AhkChord(entry.shortcut))
                    throw Error(ShortcutLabel(chord) ' is already assigned to ' MonitorLabel(profile.monitor) ' / ' entry.name '.')
}

GlobalHotkeyNames(kind, chord) {
    names := [AhkChord(chord)]
    if kind = 'brightnessDown' && chord = 'Ctrl|Alt|NumpadSub'
        names.Push('^!-')
    else if kind = 'brightnessUp' && chord = 'Ctrl|Alt|NumpadAdd'
        names.Push('^!+=')
    else if kind = 'brightnessNext' && chord = 'Ctrl|Alt|NumpadMult'
        names.Push('^!+8')
    return names
}

ExportMonitors() {
    global monitorTool
    path := A_Temp '\switchMonitor-' DllCall('GetCurrentProcessId') '-' A_TickCount '.txt'
    try {
        RunWait('"' monitorTool '" /smonitors "' path '"', , 'Hide')
        if !FileExist(path)
        throw Error('Could not retrieve the monitor list.')
        return FileRead(path)
    } finally {
        if FileExist(path)
            FileDelete(path)
    }
}

ParseMonitors(data, includeInternal := true) {
    monitors := []
    current := Map()
    for line in StrSplit(data, '`n', '`r') {
        if RegExMatch(line, '^Monitor Device Name: "([^"]+)"', &match) {
            if current.Count
                monitors.Push(BuildMonitor(current))
            current := Map('Monitor Device Name', match[1])
        } else if current.Count && RegExMatch(line, '^([^:]+):\s*"([^"]*)"', &match) {
            current[match[1]] := match[2]
        }
    }
    if current.Count
        monitors.Push(BuildMonitor(current))
    if includeInternal
        AttachInternalBrightness(monitors)
    return monitors
}

AttachInternalBrightness(monitors) {
    ; Windows exposes built-in panel brightness through WMI rather than DDC/CI.
    try {
        service := ComObjGet('winmgmts:\\.\root\wmi')
        instances := Map()
        for item in service.ExecQuery('SELECT Active, InstanceName FROM WmiMonitorBrightness') {
            if !item.Active || !RegExMatch(item.InstanceName, 'i)^(?:DISPLAY|MONITOR)\\([^\\]+)', &match)
                continue
            model := StrUpper(match[1])
            if !instances.Has(model)
                instances[model] := []
            instances[model].Push(item.InstanceName)
        }
        monitorCounts := Map()
        for monitor in monitors {
            model := MonitorHardwareCode(monitor)
            monitorCounts[model] := monitorCounts.Get(model, 0) + 1
        }
        for monitor in monitors {
            model := MonitorHardwareCode(monitor)
            if instances.Has(model) && instances[model].Length = 1 && monitorCounts[model] = 1 {
                monitor.internalBrightness := instances[model][1]
                monitor.laptopModel := LaptopModelName()
            }
        }
        for model, names in instances {
            if names.Length != 1 || monitorCounts.Get(model, 0) != 0
                continue
            instance := names[1]
            synthetic := BuildMonitor(Map('Monitor Device Name', instance,
                'Monitor Name', 'Built-in display', 'Short Monitor ID', model,
                'Monitor ID', 'MONITOR\' SubStr(instance, InStr(instance, '\') + 1)))
            synthetic.internalBrightness := instance
            synthetic.laptopModel := LaptopModelName()
            monitors.Push(synthetic)
        }
    } catch as err {
        try FileAppend('Internal display detection: ' err.Message '`n', AppPath('monitor-switch.log'), 'UTF-8')
    }
}

IsInternalDisplay(monitor) {
    return monitor.HasProp('internalBrightness') && monitor.internalBrightness != ''
}

MonitorHardwareCode(monitor) {
    return RegExMatch(monitor.id, 'i)^MONITOR\\([^\\]+)', &match)
        ? StrUpper(match[1]) : StrUpper(monitor.model)
}

BuildMonitor(fields) {
    device := fields['Monitor Device Name']
    name := fields.Get('Monitor Name', device)
    serial := fields.Get('Serial Number', '')
    model := fields.Get('Short Monitor ID', name)
    id := fields.Get('Monitor ID', device)
    ; Con serie, el perfil sobrevive a cambios de puerto. Sin serie, identidad Windows.
    identity := serial != '' ? model '|' serial : id
    key := 'Monitor_'
    Loop Parse identity
        key .= Format('{:04X}', Ord(A_LoopField))
    return {device: device, name: name, serial: serial, id: id, key: key, model: model,
        adapter: fields.Get('Adapter Name', ''), target: serial != '' ? serial : id}
}

IsLg29wk600(monitor) {
    return RegExMatch(monitor.id, 'i)MONITOR\\GSM771[45]\\') > 0
}

UsesIntelLg(monitor) {
    return IsLg29wk600(monitor) && monitor.HasProp('adapter') && InStr(monitor.adapter, 'Intel')
}

UsesAmdLg(monitor) {
    return IsLg29wk600(monitor) && monitor.HasProp('adapter') && RegExMatch(monitor.adapter, 'i)AMD|ATI|Radeon')
}

CompatibilityInfo(monitor) {
    if IsInternalDisplay(monitor)
        return {supported: true, message: 'Built-in display: brightness is controlled through Windows. Input switching is not available.'}
    if UsesIntelLg(monitor) {
        version := ''
        try version := FileGetVersion(A_WinDir '\System32\igfxsrvc.exe')
        if version != '8.15.10.4459' || A_PtrSize != 8
            return {supported: false, message: 'This Intel driver requires a different control path for the LG. The detected driver has not been validated.'}
        return {supported: true, message: 'LG with Intel: when returning from another computer, the monitor menu is synchronized with this PC input.'}
    }
    if UsesAmdLg(monitor) {
        available := A_PtrSize = 8 && FileExist(A_WinDir '\System32\atiadlxx.dll')
        return {supported: !!available, message: available
            ? 'LG with AMD: experimental method. With DP visible, select HDMI 1 and choose Switch to selected.'
            : 'The AMD driver ADL library is missing. Install the official graphics driver.'}
    }
    if IsLg29wk600(monitor)
        return {supported: false, message: 'This LG requires a GPU-specific method that is not implemented. Export diagnostics.'}
    return {supported: true, message: 'Standard DDC/CI method: compatibility depends on the monitor and connection.'}
}

TransportLabel(monitor) {
    return IsInternalDisplay(monitor) ? 'Windows WMI / built-in brightness'
        : UsesIntelLg(monitor) ? 'Intel CUI direct / LG F4 (source 0x50)'
        : UsesAmdLg(monitor) ? 'AMD ADL2 / VCP 60 (LG F4 fallback), experimental'
        : IsLg29wk600(monitor) ? 'LG: no GPU transport implemented' : 'ControlMyMonitor / VCP 60'
}

DiagnoseMonitors(monitors) {
    report := 'SwitchMonitor ' APP_VERSION ' - ' FormatTime(, 'yyyy-MM-dd HH:mm:ss')
        . '`nRead-only diagnostics; does not switch inputs.`nWindows ' A_OSVersion
        . '`nAutoHotkey ' A_AhkVersion ' / ' (A_PtrSize * 8) ' bits / compiled=' A_IsCompiled
        . '`nData: ' AppPath() '`nControlMyMonitor: ' FileGetVersion(monitorTool) '`n'
    for index, monitor in monitors {
        report .= '`nMonitor ' index ': ' monitor.name ' | ' monitor.device '`nGPU: ' monitor.adapter
            . '`nMethod: ' TransportLabel(monitor) '`n'
        report .= 'Compatibility: ' CompatibilityInfo(monitor).message '`n'
        try {
            if IsInternalDisplay(monitor) {
                report .= 'Brightness: ' BrightnessRead(monitor) '% via Windows WMI`n'
                report .= 'Inputs: none (built-in display)`n'
            } else if UsesIntelLg(monitor) {
                api := IntelLegacyDdc(monitor.device)
                report .= 'Intel UID: ' api.uid '`n'
                report .= 'Brightness: ' api.GetVcp(0x10) '`n'
                report .= 'Input: ' api.GetVcp(0x60) '`n'
            } else if UsesAmdLg(monitor) {
                api := AmdLgDdc(monitor.device, monitor.name)
                report .= 'AMD adapter=' api.adapter ' display=' api.display '`n'
                report .= 'Input via VCP 60: ' TryReadInput(monitor.target) '`n'
            } else {
                report .= 'Input: ' TryReadInput(monitor.target) '`n'
            }
        } catch as err {
            report .= 'ERROR: ' err.Message '`n'
        }
    }
    FileOpen(AppPath('monitor-diagnose.txt'), 'w', 'UTF-8').Write(report)
}

ExportDiagnostic(*) {
    global busy
    if busy
        return
    busy := true
    try {
        DiagnoseMonitors(ParseMonitors(ExportMonitors()))
        Run('notepad.exe "' AppPath('monitor-diagnose.txt') '"')
    } catch as err {
        MsgBox(err.Message, 'Diagnostics', 'Iconx')
    } finally {
        busy := false
    }
}

ReadMonitorInput(monitor, quiet := false) {
    if UsesAmdLg(monitor) {
        try return AmdLgDdc(monitor.device, monitor.name).ReadInput()
        catch as err {
            if !quiet
                FileAppend('AMD input read: ' err.Message '`n', AppPath('monitor-switch.log'), 'UTF-8')
            return 0
        }
    }
    if !UsesIntelLg(monitor)
        return TryReadInput(monitor.target)
    try {
        value := IntelLegacyDdc(monitor.device, !quiet).GetVcp(0x60)
        return HasCode([17, 18, 15], value) ? value : 0
    } catch {
        return 0
    }
}

CancelReturnSync(key := '') {
    global returnWatches
    if key = ''
        returnWatches.Clear()
    else if returnWatches.Has(key)
        returnWatches.Delete(key)
    if !returnWatches.Count
        SetTimer(CheckReturnSync, 0)
}

ArmReturnSync(monitor, previous, requested) {
    global returnWatches, returnWatchEnabled
    if !returnWatchEnabled || !UsesIntelLg(monitor)
        return
    CancelReturnSync(monitor.key)
    if !HasCode([17, 18, 15], previous) || previous = requested
        return
    returnWatches[monitor.key] := ReturnSyncState(monitor, previous)
    FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' return-sync armed input=' previous
        ' target=' monitor.target '`n', AppPath('monitor-switch.log'), 'UTF-8')
    SetTimer(CheckReturnSync, 200)
}

CheckReturnSync() {
    global returnWatches, busy
    if busy || !returnWatches.Count
        return
    busy := true
    try {
        keys := []
        for key in returnWatches
            keys.Push(key)
        for key in keys {
            if !returnWatches.Has(key)
                continue
            pending := returnWatches[key]
            try {
                decision := pending.Observe(ReadMonitorInput(pending.monitor, true))
                if decision = 'wait'
                    continue
                ; One attempt per departure, regardless of the tray selection.
                CancelReturnSync(key)
                if decision = 'cancel'
                    continue
                matches := []
                for candidate in ParseMonitors(ExportMonitors())
                    if candidate.key = pending.monitor.key
                        matches.Push(candidate)
                if matches.Length != 1 || !UsesIntelLg(matches[1])
                    continue
                monitor := matches[1]
                if ReadMonitorInput(monitor, true) != pending.input
                    continue
                IntelLegacyDdc(monitor.device).SendLgInput(pending.input)
                FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' return-sync accepted input=' pending.input
                    ' target=' monitor.target ' physical=unverified`n', AppPath('monitor-switch.log'), 'UTF-8')
                ToolTip(PortName(pending.input) ' resent to synchronize the LG menu.')
                SetTimer(() => ToolTip(), -3500)
            } catch as err {
                CancelReturnSync(key)
                FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' return-sync ERROR=' err.Message '`n',
                    AppPath('monitor-switch.log'), 'UTF-8')
            }
        }
    } finally {
        busy := false
    }
}

ControlStatus(monitor) {
    if IsInternalDisplay(monitor) {
        try return {current: BrightnessRead(monitor), message: 'Built-in display brightness is available through Windows. This screen has no switchable inputs.'}
        catch as err
            return {current: 0, message: err.Message}
    }
    compatibility := CompatibilityInfo(monitor)
    if !compatibility.supported
        return {current: 0, message: compatibility.message}
    current := ReadMonitorInput(monitor)
    if current
        return {current: current, message: 'Monitor control is available from this computer. Reported input: ' PortName(current) '.'}
    return {current: 0, message: 'This computer cannot read the current input. When another computer is shown, this connection may lose monitor control. Return to this computer with the monitor joystick and try again.'}
}

CheckSelectedControl(*) {
    global selectedMonitor, busy
    if busy || !IsObject(selectedMonitor)
        return
    busy := true
    try {
        result := ControlStatus(selectedMonitor)
        MsgBox(result.message, 'Monitor connection', result.current ? 'Iconi' : 'Icon!')
    } finally {
        busy := false
    }
}

ChangeMonitor(*) {
    global busy, wizardOpen
    if busy || wizardOpen
        return
    OpenLearning()
}

RefreshAvailableMonitors(monitors := 0) {
    global availableMonitors, selectedMonitor, uiTest, profileError
    if uiTest
        return
    currentKey := IsObject(selectedMonitor) ? selectedMonitor.key : ''
    if !IsObject(monitors)
        monitors := ParseMonitors(ExportMonitors())
    if !monitors.Length
        throw Error('No monitors detected.')
    availableMonitors := monitors
    LoadMonitorProfiles()
    SelectActiveMonitor(FindMonitorIndex(currentKey), false)
    try {
        RegisterShortcuts()
        profileError := ''
    } catch as err {
        profileError := err.Message
    }
}

MonitorTopologyChanged(*) {
    SetTimer(RefreshMonitorsAfterChange, -1500)
}

MonitorDeviceChanged(wParam, *) {
    if wParam = 7 ; DBT_DEVNODES_CHANGED
        MonitorTopologyChanged()
}

PollMonitorTopology(*) {
    RefreshMonitorsAfterChange()
}

RefreshMonitorsAfterChange(*) {
    global availableMonitors, brightnessPanel, brightnessSelectedKey, brightnessTopologySignature
        , wizardOpen, busy, uiTest
    if uiTest || wizardOpen || busy
        return
    try {
        detected := ParseMonitors(ExportMonitors())
        topology := CurrentDisplayTopologySignature()
        listChanged := MonitorListChanged(availableMonitors, detected)
        if !detected.Length || (!listChanged && topology = brightnessTopologySignature)
            return
        wasVisible := IsObject(brightnessPanel) && brightnessPanel.target != 0
        if IsObject(brightnessPanel)
            BrightnessDestroyPanel()
        if listChanged
            RefreshAvailableMonitors(detected)
        brightnessTopologySignature := topology
        active := BrightnessActiveMonitors()
        if active.Length && (!IsObject(BrightnessMonitor(brightnessSelectedKey))
            || !BrightnessMonitorActive(BrightnessMonitor(brightnessSelectedKey)))
            brightnessSelectedKey := active[1].key
        BrightnessStartRangeJobs()
        if wasVisible
            ShowBrightnessPanel()
    } catch as err {
        FileAppend('Monitor refresh: ' err.Message '`n', AppPath('monitor-switch.log'), 'UTF-8')
    }
}

CurrentDisplayTopologySignature() {
    names := []
    Loop MonitorGetCount()
        names.Push(StrUpper(MonitorGetName(A_Index)))
    signature := ''
    for name in names
        signature .= '|' name
    return signature
}

MonitorListChanged(previous, current) {
    if previous.Length != current.Length
        return true
    for index, monitor in current
        if monitor.key != previous[index].key
            || monitor.device != previous[index].device
            || monitor.target != previous[index].target
            || IsInternalDisplay(monitor) != IsInternalDisplay(previous[index])
            return true
    return false
}

LoadAssignments(path, key) {
    entries := Map()
    entries.disabled := []
    entries.connected := []
    for raw in StrSplit(IniRead(path, key, 'ConnectedCodes', ''), ',')
        if RegExMatch(raw, '^\d+$') && Integer(raw) >= 1 && Integer(raw) <= 65535
            entries.connected.Push(Integer(raw))
    for raw in StrSplit(IniRead(path, key, 'DisabledCodes', ''), ',')
        if RegExMatch(raw, '^\d+$')
            entries.disabled.Push(Integer(raw))
    Loop 9 {
        slot := A_Index
        code := IniRead(path, key, 'Slot' slot 'Code', '')
        name := IniRead(path, key, 'Slot' slot 'Name', '')
        if RegExMatch(code, '^\d+$') && Integer(code) >= 1 && Integer(code) <= 65535 && name != ''
            entries[slot] := {name: name, code: Integer(code), shortcut: IniRead(path, key, 'Slot' slot 'Shortcut', 'Ctrl|Alt|' slot)}
    }
    return entries
}

SaveAssignments(path, monitor, entries) {
    temporary := path '.new-' DllCall('GetCurrentProcessId')
    try {
        if FileExist(path)
            FileCopy(path, temporary, 1)
        if FileExist(temporary) && InStr('`n' IniRead(temporary) '`n', '`n' monitor.key '`n')
            IniDelete(temporary, monitor.key)
        IniWrite(monitor.name, temporary, monitor.key, 'Name')
        IniWrite(monitor.id, temporary, monitor.key, 'MonitorID')
        disabled := ''
        if entries.HasProp('disabled')
            for code in entries.disabled
                disabled .= (disabled = '' ? '' : ',') code
        IniWrite(disabled, temporary, monitor.key, 'DisabledCodes')
        connected := ''
        if entries.HasProp('connected')
            for code in entries.connected
                connected .= (connected = '' ? '' : ',') code
        IniWrite(connected, temporary, monitor.key, 'ConnectedCodes')
        for slot, entry in entries {
            IniWrite(entry.name, temporary, monitor.key, 'Slot' slot 'Name')
            IniWrite(entry.code, temporary, monitor.key, 'Slot' slot 'Code')
            IniWrite(entry.HasProp('shortcut') ? entry.shortcut : 'Ctrl|Alt|' slot, temporary, monitor.key, 'Slot' slot 'Shortcut')
        }
        FileMove(temporary, path, 1)
    } finally {
        if FileExist(temporary)
            FileDelete(temporary)
    }
}

RegisterShortcuts() {
    global monitorProfiles, registeredBindings
    ValidateProfileShortcuts(monitorProfiles)
    for binding in registeredBindings {
        HotIf(binding.condition)
        Hotkey(binding.key, 'Off')
    }
    registeredBindings := []
    for key, profile in monitorProfiles {
        for slot, entry in profile.assignments {
            chord := ValidateChord(entry.HasProp('shortcut') ? entry.shortcut : 'Ctrl|Alt|' slot)
            keys := StrSplit(chord, '|')
            for token in keys {
                condition := ChordHeld.Bind(keys, token)
                for trigger in TriggerKeys(token) {
                    HotIf(condition)
                    hotkeyName := '*' trigger
                    Hotkey(hotkeyName, FireChord.Bind(key, slot, trigger), 'On')
                    registeredBindings.Push({key: hotkeyName, condition: condition})
                }
            }
        }
    }
    HotIf()
}

ValidateProfileShortcuts(profiles) {
    seen := Map()
    for key, profile in profiles
        for slot, entry in profile.assignments {
            chord := ValidateChord(entry.HasProp('shortcut') ? entry.shortcut : 'Ctrl|Alt|' slot)
            if seen.Has(chord) {
                previous := seen[chord]
                throw Error('Shortcut ' ShortcutLabel(chord) ' is duplicated between '
                    MonitorLabel(previous.monitor) ' / ' previous.name ' and '
                    MonitorLabel(profile.monitor) ' / ' entry.name '.')
            }
            seen[chord] := {monitor: profile.monitor, name: entry.name}
        }
}

ProfilesWithDrafts(monitors, drafts, path) {
    profiles := Map()
    for monitor in monitors {
        if drafts.Has(monitor.key) {
            rows := drafts[monitor.key]
            profiles[monitor.key] := {monitor: monitor, rows: rows, assignments: RowsToAssignments(rows)}
        } else {
            saved := LoadAssignments(path, monitor.key)
            if saved.Count
                profiles[monitor.key] := {monitor: monitor, rows: [], assignments: saved}
        }
    }
    return profiles
}

AvoidDefaultShortcutConflicts(rows, saved, monitor, drafts, monitors, path) {
    occupied := Map()
    for key, profile in ProfilesWithDrafts(monitors, drafts, path)
        if key != monitor.key
            for slot, entry in profile.assignments
                occupied[ValidateChord(entry.shortcut)] := true
    for row in rows {
        if row.shortcut = ''
            continue
        explicit := row.slot && saved.Has(row.slot) && saved[row.slot].code = row.code
        if !explicit && occupied.Has(row.shortcut) {
            Loop 9 {
                candidate := 'Ctrl|Alt|' A_Index
                taken := occupied.Has(candidate)
                for other in rows
                    if other != row && other.shortcut = candidate
                        taken := true
                if !taken {
                    row.shortcut := candidate
                    break
                }
            }
            if occupied.Has(row.shortcut)
                row.shortcut := ''
        }
        if row.shortcut != ''
            occupied[row.shortcut] := true
    }
}

FireChord(monitorKey, slot, trigger, *) {
    SelectSlot(monitorKey, slot)
    KeyWait(trigger)
}

NormalizeKey(name) {
    aliases := Map('LControl', 'Ctrl', 'RControl', 'Ctrl', 'LCtrl', 'Ctrl', 'RCtrl', 'Ctrl', 'Control', 'Ctrl', 'LAlt', 'Alt', 'RAlt', 'Alt', 'LShift', 'Shift', 'RShift', 'Shift', 'LWin', 'Win', 'RWin', 'Win')
    return aliases.Get(name, StrLen(name) = 1 ? StrUpper(name) : name)
}

TriggerKeys(token) {
    aliases := Map('Ctrl', ['LCtrl', 'RCtrl'], 'Alt', ['LAlt', 'RAlt'], 'Shift', ['LShift', 'RShift'], 'Win', ['LWin', 'RWin'])
    return aliases.Get(token, [token])
}

TokenHeld(token) {
    for key in TriggerKeys(token)
        if GetKeyState(key, 'P')
            return true
    return false
}

ChordHeld(keys, trigger, *) {
    global busy, wizardOpen
    if busy || wizardOpen
        return false
    for key in keys
        if key != trigger && !TokenHeld(key)
            return false
    for modifier in ['Ctrl', 'Alt', 'Shift', 'Win']
        if !HasKeyToken(keys, modifier) && TokenHeld(modifier)
            return false
    return true
}

HasKeyToken(keys, key) {
    for item in keys
        if item = key
            return true
    return false
}

ValidateChord(text) {
    keys := []
    for raw in StrSplit(text, '|') {
        name := NormalizeKey(Trim(raw))
        if name = '' || HasKeyToken(keys, name)
            continue
        if name != 'Win' && !GetKeyVK(name) && !GetKeySC(name)
            throw Error('Unrecognized key: ' name)
        keys.Push(name)
    }
    if keys.Length < 2 || keys.Length > 4
        throw Error('Press 2 to 4 keys together for a shortcut.')
    canonical := ''
    for modifier in ['Ctrl', 'Alt', 'Shift', 'Win']
        if HasKeyToken(keys, modifier)
            canonical .= (canonical = '' ? '' : '|') modifier
    regular := ''
    for key in keys
        if !HasKeyToken(['Ctrl', 'Alt', 'Shift', 'Win'], key)
            regular .= key '`n'
    for key in StrSplit(Sort(Trim(regular, '`n')), '`n')
        if key != ''
            canonical .= (canonical = '' ? '' : '|') key
    return canonical
}

ShortcutLabel(chord) {
    if chord = 'Ctrl|Alt|NumpadSub'
        return 'Ctrl+Alt+-'
    if chord = 'Ctrl|Alt|NumpadAdd'
        return 'Ctrl+Alt++'
    if chord = 'Ctrl|Alt|NumpadMult'
        return 'Ctrl+Alt+*'
    return chord = '' ? 'None' : StrReplace(StrReplace(chord, 'Ctrl|Alt|Shift', 'Ctrl|Shift|Alt'), '|', '+')
}

SolidButton(window, options, label, color := '0E639C') {
    global buttonStyles
    static installed := false
    if !installed {
        OnMessage(0x2B, PaintSolidButton)
        SetTimer(RefreshButtonHover, 50)
        installed := true
    }
    control := window.AddButton(options, label)
    control.SetFont('s10 Bold')
    styleBits := DllCall('user32\GetWindowLongPtrW', 'Ptr', control.Hwnd, 'Int', -16, 'Ptr')
    DllCall('user32\SetWindowLongPtrW', 'Ptr', control.Hwnd, 'Int', -16,
        'Ptr', (styleBits & ~0xF) | 0xB, 'Ptr')
    DllCall('user32\SetWindowPos', 'Ptr', control.Hwnd, 'Ptr', 0,
        'Int', 0, 'Int', 0, 'Int', 0, 'Int', 0, 'UInt', 0x27)
    hover := Map('0E639C', '1177BB', '3C3C3C', '525252', '2D2D2D', '444444').Get(color, '525252')
    buttonStyles[control.Hwnd] := {control: control, fill: color, hover: hover, hovered: false}
    DllCall('user32\InvalidateRect', 'Ptr', control.Hwnd, 'Ptr', 0, 'Int', 0)
    return control
}

SetSolidButtonImage(control, path, size := 14, offsetX := 0, offsetY := 0) {
    global buttonStyles, buttonImages, gdipToken
    if !gdipToken {
        ; GdiplusStartupInput is 24 bytes on x64 (pointer alignment), 16 on x86.
        input := Buffer(A_PtrSize = 8 ? 24 : 16, 0)
        NumPut('UInt', 1, input, 0)
        token := Buffer(A_PtrSize, 0)
        status := DllCall('gdiplus\GdiplusStartup', 'Ptr', token, 'Ptr', input, 'Ptr', 0, 'UInt')
        if status != 0
            throw Error('Could not initialize image rendering (GDI+ status ' status ').')
        gdipToken := NumGet(token, 0, 'Ptr')
        OnExit(CleanupButtonImages)
    }
    if !buttonImages.Has(path) {
        output := Buffer(A_PtrSize, 0)
        if RegExMatch(path, 'i)\.ico$') {
            handle := DllCall('user32\LoadImageW', 'Ptr', 0, 'Str', path,
                'UInt', 1, 'Int', 32, 'Int', 32, 'UInt', 0x10, 'Ptr')
            if !handle
                throw Error('Could not load icon: ' path)
            try status := DllCall('gdiplus\GdipCreateBitmapFromHICON',
                'Ptr', handle, 'Ptr', output, 'UInt')
            finally DllCall('user32\DestroyIcon', 'Ptr', handle)
        } else
            status := DllCall('gdiplus\GdipLoadImageFromFile',
                'WStr', path, 'Ptr', output, 'UInt')
        if status != 0
            throw Error('Could not load icon: ' path)
        buttonImages[path] := NumGet(output, 0, 'Ptr')
    }
    buttonStyles[control.Hwnd].icon := buttonImages[path]
    buttonStyles[control.Hwnd].iconSize := size
    buttonStyles[control.Hwnd].iconOffsetX := offsetX
    buttonStyles[control.Hwnd].iconOffsetY := offsetY
    buttonStyles[control.Hwnd].roundIcon := true
    buttonStyles[control.Hwnd].iconOpacity := 1.0
    buttonStyles[control.Hwnd].iconHoverOpacity := 1.0
    DllCall('user32\InvalidateRect', 'Ptr', control.Hwnd, 'Ptr', 0, 'Int', 0)
}

CleanupButtonImages(*) {
    global buttonImages
    for _, image in buttonImages
        DllCall('gdiplus\GdipDisposeImage', 'Ptr', image)
    ; Windows releases GDI+ when the process exits. Explicit shutdown faults here.
}

GdiColor(hex) {
    rgb := Integer('0x' hex)
    return ((rgb & 0xFF0000) >> 16) | (rgb & 0x00FF00) | ((rgb & 0x0000FF) << 16)
}

PaintSolidButton(wParam, lParam, *) {
    global buttonStyles
    hwnd := NumGet(lParam, 24, 'Ptr')
    if !buttonStyles.Has(hwnd)
        return
    style := buttonStyles[hwnd]
    dc := NumGet(lParam, 32, 'Ptr')
    left := NumGet(lParam, 40, 'Int')
    top := NumGet(lParam, 44, 'Int')
    right := NumGet(lParam, 48, 'Int')
    bottom := NumGet(lParam, 52, 'Int')
    background := DllCall('gdi32\CreateSolidBrush', 'UInt', GdiColor('1E1E1E'), 'Ptr')
    DllCall('user32\FillRect', 'Ptr', dc, 'Ptr', lParam + 40, 'Ptr', background)
    DllCall('gdi32\DeleteObject', 'Ptr', background)
    if !style.HasOwnProp('roundIcon') {
        brush := DllCall('gdi32\CreateSolidBrush', 'UInt', GdiColor(style.hovered ? style.hover : style.fill), 'Ptr')
        oldBrush := DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', brush, 'Ptr')
        oldPen := DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', DllCall('gdi32\GetStockObject', 'Int', 8, 'Ptr'), 'Ptr')
        DllCall('gdi32\RoundRect', 'Ptr', dc, 'Int', left, 'Int', top, 'Int', right, 'Int', bottom, 'Int', 24, 'Int', 24)
        DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', oldPen)
        DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', oldBrush)
        DllCall('gdi32\DeleteObject', 'Ptr', brush)
    }
    DllCall('gdi32\SetBkMode', 'Ptr', dc, 'Int', 1)
    disabled := NumGet(lParam, 16, 'UInt') & 4
    DllCall('gdi32\SetTextColor', 'Ptr', dc, 'UInt', disabled ? 0xAAAAAA : 0xFFFFFF)
    font := SendMessage(0x31, 0, 0, hwnd)
    oldFont := font ? DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', font, 'Ptr') : 0
    DllCall('user32\DrawTextW', 'Ptr', dc, 'Str', style.control.Text, 'Int', -1,
        'Ptr', lParam + 40, 'UInt', 0x8025)
    if oldFont
        DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', oldFont)
    if style.HasOwnProp('icon') {
        iconSize := style.iconSize
        output := Buffer(A_PtrSize, 0)
        ; Commit the GDI background before GDI+ blends a dimmed tray icon.
        DllCall('gdi32\GdiFlush')
        if DllCall('gdiplus\GdipCreateFromHDC', 'Ptr', dc, 'Ptr', output, 'UInt') = 0 {
            graphics := NumGet(output, 0, 'Ptr')
            DllCall('gdiplus\GdipSetInterpolationMode', 'Ptr', graphics, 'Int', 7)
            x := left + Round((right - left - iconSize) / 2) + style.iconOffsetX
            y := top + Round((bottom - top - iconSize) / 2) + style.iconOffsetY
            opacity := style.hovered ? style.iconHoverOpacity : style.iconOpacity
            DrawButtonImage(graphics, style.icon, x, y, iconSize, opacity,
                left, top, right - left, bottom - top)
            DllCall('gdiplus\GdipDeleteGraphics', 'Ptr', graphics)
        }
    }
    return true
}

DrawButtonImage(graphics, icon, x, y, size, opacity, left, top, width, height) {
    ; Paint an opaque base in the same GDI+ pass as the icon. Otherwise its
    ; translucent dimming layer can blend with an unpainted white button DC.
    base := Buffer(A_PtrSize, 0)
    if DllCall('gdiplus\GdipCreateSolidFill', 'UInt', 0xFF1E1E1E,
        'Ptr', base, 'UInt') = 0 {
        handle := NumGet(base, 0, 'Ptr')
        DllCall('gdiplus\GdipFillRectangleI', 'Ptr', graphics, 'Ptr', handle,
            'Int', left, 'Int', top, 'Int', width, 'Int', height)
        DllCall('gdiplus\GdipDeleteBrush', 'Ptr', handle)
    }
    DllCall('gdiplus\GdipDrawImageRectI', 'Ptr', graphics, 'Ptr', icon,
        'Int', x, 'Int', y, 'Int', size, 'Int', size)
    if opacity >= 1
        return
    brush := Buffer(A_PtrSize, 0)
    overlay := (Round((1 - opacity) * 255) << 24) | 0x1E1E1E
    if DllCall('gdiplus\GdipCreateSolidFill', 'UInt', overlay, 'Ptr', brush, 'UInt') != 0
        return
    handle := NumGet(brush, 0, 'Ptr')
    DllCall('gdiplus\GdipFillRectangleI', 'Ptr', graphics, 'Ptr', handle,
        'Int', left, 'Int', top, 'Int', width, 'Int', height)
    DllCall('gdiplus\GdipDeleteBrush', 'Ptr', handle)
}

RefreshButtonHover() {
    global buttonStyles, buttonHoverHwnd
    MouseGetPos(, , , &hovered, 2)
    if hovered = buttonHoverHwnd
        return
    previous := buttonHoverHwnd
    buttonHoverHwnd := buttonStyles.Has(hovered) ? hovered : 0
    for hwnd in [previous, buttonHoverHwnd] {
        if !buttonStyles.Has(hwnd)
            continue
        if !DllCall('user32\IsWindow', 'Ptr', hwnd) {
            buttonStyles.Delete(hwnd)
            continue
        }
        buttonStyles[hwnd].hovered := hwnd = buttonHoverHwnd
        DllCall('user32\InvalidateRect', 'Ptr', hwnd, 'Ptr', 0, 'Int', 0)
    }
}

RoundControls(controls) {
    global buttonStyles
    for item in controls {
        if buttonStyles.Has(item.control.Hwnd)
            continue
        region := DllCall('gdi32\CreateRoundRectRgn', 'Int', 0, 'Int', 0,
            'Int', item.width, 'Int', item.height, 'Int', 24, 'Int', 24, 'Ptr')
        if region
            DllCall('user32\SetWindowRgn', 'Ptr', item.control.Hwnd, 'Ptr', region, 'Int', 1)
    }
}

ReadInput() {
    global monitorTool, selectedMonitor
    target := selectedMonitor.target
    Loop 3 {
        value := RunWait('"' monitorTool '" /GetValue "' target '" 60', , 'Hide')
        if value >= 1 && value <= 65535
            return value
        Sleep(400)
    }
    throw Error('Could not read the selected monitor input. Select that input with the monitor controls, then run setup from the connected computer.')
}

SelectSlot(monitorKey, slot, *) {
    global monitorProfiles, busy, wizardOpen
    if busy || wizardOpen || !monitorProfiles.Has(monitorKey)
        return
    profile := monitorProfiles[monitorKey]
    if !profile.assignments.Has(slot)
        return
    SendInputCommand(profile.monitor, profile.assignments[slot])
}

SendInputCommand(monitor, entry, knownBefore := -1, showMessage := true,
    armReturnWatch := true) {
    global monitorTool, busy
    if busy
        return
    busy := true
    try {
        compatibility := CompatibilityInfo(monitor)
        if !compatibility.supported
            throw Error(compatibility.message)
        if UsesIntelLg(monitor) || UsesAmdLg(monitor) {
            amd := UsesAmdLg(monitor)
            previous := UsesIntelLg(monitor) ? (knownBefore >= 0 ? knownBefore : ReadMonitorInput(monitor)) : 0
            api := amd ? AmdLgDdc(monitor.device, monitor.name) : IntelLegacyDdc(monitor.device)
            if amd {
                ; On the tested AMD LG, F4 was acknowledged but left the input
                ; on DP. VCP 60 initiated the physical switch, so send it first.
                api.SendStandardInput(entry.code)
                ; ADL_OK means the driver accepted the packet, not that the LG
                ; actually selected the requested input. Read back when possible.
                observed := 0
                Loop 2 {
                    Sleep(A_Index = 1 ? 280 : 350)
                    try observed := api.ReadInput()
                    catch as err {
                        FileAppend('AMD input confirmation: ' err.Message '`n',
                            AppPath('monitor-switch.log'), 'UTF-8')
                        observed := 0
                    }
                    if observed = entry.code || !observed
                        break
                }
                if observed && observed != entry.code {
                    api.SendLgInput(entry.code)
                    Sleep(400)
                    try observed := api.ReadInput()
                    catch as err {
                        FileAppend('AMD LG F4 confirmation: ' err.Message '`n',
                            AppPath('monitor-switch.log'), 'UTF-8')
                        observed := 0
                    }
                    if observed && observed != entry.code
                        throw Error('AMD accepted both input commands, but the LG still reports '
                            PortName(observed) '. The input did not change.')
                }
            } else
                api.SendLgInput(entry.code)
            if armReturnWatch
                ArmReturnSync(monitor, previous, entry.code)
            RememberInputSwitch(monitor, knownBefore >= 0 ? knownBefore : previous, entry.code)
            FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' target=' monitor.target ' transport=' TransportLabel(monitor) ' requested='
                entry.code ' accepted=1 physical=unverified`n',
                AppPath('monitor-switch.log'), 'UTF-8')
            if showMessage {
                ToolTip('Requested ' entry.name '. Check the image and the monitor input menu.')
                SetTimer(() => ToolTip(), -3500)
            }
            return true
        }
        before := knownBefore >= 0 ? knownBefore : TryReadInput(monitor.target)
        code := RunWait('"' monitorTool '" /SetValue "' monitor.target '" 60 ' entry.code, , 'Hide')
        if code != 0
            throw Error('ControlMyMonitor returned code ' code '.')
        RememberInputSwitch(monitor, before, entry.code)
        Sleep(1200)
        after := TryReadInput(monitor.target)
        FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' target=' monitor.target ' requested=' entry.code ' before=' before ' after=' after ' exit=' code '`n', AppPath('monitor-switch.log'), 'UTF-8')
        if after = entry.code
            ToolTip('Monitor reports: ' entry.name '. This does not confirm the physical display state.')
        else if after > 0
            ToolTip('Switch unconfirmed: requested ' entry.code ', monitor still reports ' after '.')
        else
            ToolTip('Command sent to ' entry.name ', but no follow-up reading is available. Check the monitor menu.')
        SetTimer(() => ToolTip(), -7000)
        return true
    } catch as err {
        FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' target=' monitor.target ' requested=' entry.code
            ' ERROR=' err.Message '`n', AppPath('monitor-switch.log'), 'UTF-8')
        if showMessage {
            detail := IsLg29wk600(monitor)
                ? '`n`nThis PC cannot send DDC/CI commands while the LG shows another input. Switch back from the displayed PC if its monitor control is configured, or use the monitor joystick. The previous input remains saved for a later attempt.'
                : ''
            MsgBox(err.Message detail, 'Switch input', 'Iconx')
        }
        return false
    } finally {
        busy := false
    }
}

RememberInputSwitch(monitor, previous, requested) {
    global inputReturnHistory, inputReturnFile, selectedMonitor
    if previous <= 0 || previous = requested
        return
    inputReturnHistory[monitor.key] := {origin: previous, destination: requested,
        tick: A_TickCount}
    try {
        IniWrite(previous ',' requested, inputReturnFile, monitor.key, 'Switch')
    } catch as err {
        FileAppend('Could not save the previous monitor input: ' err.Message '`n',
            AppPath('monitor-switch.log'), 'UTF-8')
    }
    if IsObject(selectedMonitor) && selectedMonitor.key = monitor.key
        BuildTrayMenu()
}

LoadInputReturnHistory() {
    global inputReturnHistory, inputReturnFile, availableMonitors
    inputReturnHistory.Clear()
    for monitor in availableMonitors {
        pair := StrSplit(IniRead(inputReturnFile, monitor.key, 'Switch', ''), ',')
        if pair.Length != 2
            continue
        origin := pair[1]
        destination := pair[2]
        if RegExMatch(origin, '^\d+$') && RegExMatch(destination, '^\d+$')
            && Integer(origin) > 0 && Integer(destination) > 0
            && Integer(origin) != Integer(destination)
            inputReturnHistory[monitor.key] := {origin: Integer(origin),
                destination: Integer(destination), tick: -10001}
    }
}

ForgetInputSwitch(monitor) {
    global inputReturnHistory, inputReturnFile
    if inputReturnHistory.Has(monitor.key)
        inputReturnHistory.Delete(monitor.key)
    try IniDelete(inputReturnFile, monitor.key)
    BuildTrayMenu()
}

FindReturnInput(rows, current, history) {
    if !IsObject(history) || current != history.destination
        return 0
    for row in rows
        if row.code = history.origin && row.connected
            return row
    return 0
}

ChooseCycleInput(rows, current, history, now) {
    recent := IsObject(history) && now - history.tick >= 0
        && now - history.tick <= 10000
    returning := recent ? FindReturnInput(rows, current, history) : 0
    return IsObject(returning)
        ? {entry: returning, reversing: true}
        : {entry: FindNextConnected(rows, current), reversing: false}
}

ReturnPreviousInput(*) {
    global selectedMonitor, cycleEntries, inputReturnHistory, busy, wizardOpen
    if busy || wizardOpen || !IsObject(selectedMonitor)
        return
    monitor := selectedMonitor
    if !inputReturnHistory.Has(monitor.key)
        return
    history := inputReturnHistory[monitor.key]
    result := ControlStatus(monitor)
    previous := FindReturnInput(cycleEntries,
        result.current ? result.current : history.destination, history)
    if !IsObject(previous) {
        ForgetInputSwitch(monitor)
        MsgBox('The input changed or the previous port is no longer marked as connected. No command was sent.',
            'Return to previous input', 'Icon!')
        return
    }
    if !result.current
        FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' input return with unreadable current source target='
            monitor.target ' destination=' history.destination ' origin=' history.origin '`n',
            AppPath('monitor-switch.log'), 'UTF-8')
    if SendInputCommand(monitor, previous, result.current ? result.current : history.destination,
        true, !!result.current) {
        MarkMainDisplayDeparted(monitor.key)
        ForgetInputSwitch(monitor)
    }
}

TryReadInput(target := '') {
    global monitorTool, selectedMonitor
    if target = ''
        target := selectedMonitor.target
    value := RunWait('"' monitorTool '" /GetValue "' target '" 60', , 'Hide')
    return value >= 1 && value <= 65535 ? value : 0
}

FindNextConnected(rows, current) {
    position := 0
    count := 0
    for index, row in rows {
        if row.code = current
            position := index
        if row.connected
            count += 1
    }
    if !count
        throw Error('Check the connected inputs in Settings, then choose Save and activate.')
    if !position
        throw Error('The monitor reports an unknown input (' current '). No switch was sent.')
    Loop rows.Length {
        index := Mod(position + A_Index - 1, rows.Length) + 1
        if rows[index].connected
            return rows[index]
    }
}

NextConnected(*) {
    global selectedMonitor, cycleEntries, busy, wizardOpen, wizardCycle, globalShortcuts
    if busy
        return
    if wizardOpen {
        if IsObject(wizardCycle)
            wizardCycle.Call()
    } else if IsObject(selectedMonitor) {
        CycleConnected(selectedMonitor, cycleEntries)
    }
    if globalShortcuts['cycle'] != '' {
        keys := StrSplit(globalShortcuts['cycle'], '|')
        KeyWait(keys[keys.Length])
    }
}

MainDisplayHasExtendedLayout() {
    if MonitorGetCount() < 2
        return false
    positions := Map()
    Loop MonitorGetCount() {
        mode := Buffer(220, 0)
        NumPut('UShort', mode.Size, mode, 68)
        if !DllCall('user32\EnumDisplaySettingsExW', 'Str', MonitorGetName(A_Index),
            'Int', -1, 'Ptr', mode, 'UInt', 0, 'Int')
            return false
        position := NumGet(mode, 76, 'Int') ',' NumGet(mode, 80, 'Int')
        if positions.Has(position)
            return false ; Cloned screens do not have independent desktops.
        positions[position] := true
    }
    return positions.Count > 1
}

SaveMainDisplayHandoff() {
    global mainDisplayHandoff, mainDisplayHandoffFile
    try {
        if IsObject(mainDisplayHandoff) {
            pending := mainDisplayHandoff
            IniWrite(pending.monitorKey '|' pending.alternateKey '|'
                pending.originInput '|' (pending.departed ? '1' : '0'),
                mainDisplayHandoffFile, 'Return', 'State')
        } else if FileExist(mainDisplayHandoffFile)
            IniDelete(mainDisplayHandoffFile, 'Return')
    } catch as err {
        FileAppend('Main display handoff state: ' err.Message '`n',
            AppPath('monitor-switch.log'), 'UTF-8')
    }
}

LoadMainDisplayHandoff() {
    global mainDisplayHandoff, mainDisplayHandoffFile
    parts := StrSplit(IniRead(mainDisplayHandoffFile, 'Return', 'State', ''), '|')
    if parts.Length != 4 || !RegExMatch(parts[3], '^\d+$')
        return
    mainDisplayHandoff := {monitorKey: parts[1], alternateKey: parts[2],
        originInput: Integer(parts[3]), departed: parts[4] = '1',
        lastRestoreAttempt: 0, misses: 0, matches: 0}
}

ClearMainDisplayHandoff() {
    global mainDisplayHandoff
    mainDisplayHandoff := 0
    SetTimer(CheckMainDisplayHandoff, 0)
    SaveMainDisplayHandoff()
}

MarkMainDisplayDeparted(key := '') {
    global mainDisplayHandoff
    if !IsObject(mainDisplayHandoff) || mainDisplayHandoff.departed
        || (key != '' && mainDisplayHandoff.monitorKey != key)
        return
    mainDisplayHandoff.departed := true
    SaveMainDisplayHandoff()
}

PrepareMainDisplayHandoff(monitor, originInput) {
    global availableMonitors, mainDisplayHandoff
    if IsObject(mainDisplayHandoff) || originInput <= 0
        || !MainDisplayHasExtendedLayout()
        return false
    displayName := BrightnessDisplayName(monitor)
    if displayName = '' || StrUpper(displayName) != StrUpper(BrightnessMainDisplayName())
        return false
    for alternate in availableMonitors {
        if alternate.key = monitor.key || !BrightnessCanBeMainDisplay(alternate)
            continue
        result := BrightnessSetMainDisplay(alternate.key)
        if !result.ok
            throw Error('The input was not changed because Windows could not move the main display: '
                result.error)
        mainDisplayHandoff := {monitorKey: monitor.key,
            alternateKey: alternate.key, originInput: originInput,
            departed: false, lastRestoreAttempt: 0, misses: 0, matches: 0}
        SaveMainDisplayHandoff()
        SetTimer(CheckMainDisplayHandoff, 1500)
        Sleep(150) ; Let the graphics driver settle before sending DDC/CI.
        return true
    }
    return false
}

AbortMainDisplayHandoff(monitor) {
    global mainDisplayHandoff
    if !IsObject(mainDisplayHandoff) || mainDisplayHandoff.monitorKey != monitor.key
        return
    result := BrightnessSetMainDisplay(monitor.key)
    if result.ok
        ClearMainDisplayHandoff()
    else {
        MarkMainDisplayDeparted(monitor.key)
        FileAppend('Main display handoff rollback: ' result.error '`n',
            AppPath('monitor-switch.log'), 'UTF-8')
    }
}

CheckMainDisplayHandoff(*) {
    try CheckMainDisplayHandoffCore()
    catch as err {
        FileAppend('Main display return check: ' err.Message '`n',
            AppPath('monitor-switch.log'), 'UTF-8')
    }
}

CheckMainDisplayHandoffCore() {
    global mainDisplayHandoff, busy, availableMonitors
    if !IsObject(mainDisplayHandoff) || busy
        return
    pending := mainDisplayHandoff
    if !MainDisplayHasExtendedLayout() {
        if MonitorGetCount() < 2
            MarkMainDisplayDeparted()
        return
    }
    original := BrightnessMonitor(pending.monitorKey)
    alternate := BrightnessMonitor(pending.alternateKey)
    if !IsObject(original) {
        MarkMainDisplayDeparted()
        return
    }
    if !IsObject(alternate)
        return
    primary := StrUpper(BrightnessMainDisplayName())
    if primary = StrUpper(BrightnessDisplayName(original)) {
        ClearMainDisplayHandoff() ; Windows already restored it.
        return
    }
    if primary != StrUpper(BrightnessDisplayName(alternate)) {
        ClearMainDisplayHandoff() ; Respect a manual change of main display.
        return
    }
    current := ReadMonitorInput(original, true)
    if current != pending.originInput {
        pending.matches := 0
        if current = 0 {
            pending.misses += 1
            if pending.misses >= 2
                MarkMainDisplayDeparted()
        } else
            MarkMainDisplayDeparted()
        return
    }
    pending.misses := 0
    if !pending.departed
        return
    pending.matches += 1
    if pending.matches < 2 || A_TickCount - pending.lastRestoreAttempt < 5000
        return
    pending.lastRestoreAttempt := A_TickCount
    result := BrightnessSetMainDisplay(original.key)
    if result.ok {
        FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss')
            ' main display restored after input return target=' original.target '`n',
            AppPath('monitor-switch.log'), 'UTF-8')
        ClearMainDisplayHandoff()
    }
}

CycleConnected(monitor, rows) {
    global busy, inputReturnHistory
    if busy
        return
    busy := true
    try {
        connected := 0
        for row in rows
            if row.connected
                connected += 1
        if !connected
            throw Error('Check the connected inputs in Settings, then choose Save and activate.')
        result := ControlStatus(monitor)
        current := result.current
        history := inputReturnHistory.Has(monitor.key)
            ? inputReturnHistory[monitor.key] : 0
        if !current {
            returning := IsObject(history)
                ? FindReturnInput(rows, history.destination, history) : 0
            if !IsObject(returning)
                throw Error(result.message '`n`nNo previous input is available for a safe return. No switch was sent.')
            busy := false
            FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' input cycle return with unreadable current source target='
                monitor.target ' destination=' history.destination ' origin=' history.origin '`n',
                AppPath('monitor-switch.log'), 'UTF-8')
            if SendInputCommand(monitor, returning, history.destination, true, false) {
                MarkMainDisplayDeparted(monitor.key)
                ForgetInputSwitch(monitor)
            }
            return
        }
        choice := ChooseCycleInput(rows, current, history, A_TickCount)
        next := choice.entry
        if next.code = current {
            ToolTip('No other input is marked as connected.')
            SetTimer(() => ToolTip(), -3000)
            return
        }
        busy := false
        handedOff := PrepareMainDisplayHandoff(monitor, current)
        sent := SendInputCommand(monitor, next, current)
        if !sent && handedOff
            AbortMainDisplayHandoff(monitor)
        if sent && choice.reversing {
            MarkMainDisplayDeparted(monitor.key)
            ForgetInputSwitch(monitor)
        }
    } catch as err {
        MsgBox(err.Message, 'Next connected input', 'Iconx')
    } finally {
        busy := false
    }
}

ExportValues(target) {
    global monitorTool
    ; Consultar capacidades anunciadas y reintentar lecturas fallidas.
    directory := AppPath('.monitor-reader')
    DirCreate(directory)
    reader := directory '\ControlMyMonitor.exe'
    if !FileExist(reader) || FileGetTime(reader) != FileGetTime(monitorTool)
        FileCopy(monitorTool, reader, 1)
    config := directory '\ControlMyMonitor.cfg'
    FileOpen(config, 'w', 'UTF-16').Write('[General]`r`nLoadingMode=1`r`nTryAgainOnFailure=1`r`nAddExportHeaderLine=1`r`nUseOnlyOneByte=1`r`n')
    path := A_Temp '\switchMonitor-values-' DllCall('GetCurrentProcessId') '-' A_TickCount '.txt'
    try {
        RunWait('"' reader '" /stab "' path '" "' target '"', directory, 'Hide')
        return FileExist(path) ? FileRead(path) : ''
    } finally {
        if FileExist(path)
            FileDelete(path)
    }
}

ParseInputs(data) {
    values := []
    for line in StrSplit(data, '`n', '`r') {
        cols := StrSplit(line, '`t')
        if cols.Length < 6 || Trim(cols[1], '" ') != '60'
            continue
        for raw in StrSplit(Trim(cols[6], '" '), ',') {
            raw := Trim(raw)
            if RegExMatch(raw, '^\d+$') && Integer(raw) >= 1 && Integer(raw) <= 65535 && !HasCode(values, Integer(raw))
                values.Push(Integer(raw))
        }
        break
    }
    return values
}

HasCode(values, code) {
    for value in values
        if value = code
            return true
    return false
}

PortName(code) {
    names := Map(1, 'VGA 1', 2, 'VGA 2', 3, 'DVI 1', 4, 'DVI 2', 15, 'DisplayPort 1', 16, 'DisplayPort 2', 17, 'HDMI 1', 18, 'HDMI 2')
    return names.Get(code, 'Input ' code)
}

OrderedCodes(values) {
    sorted := []
    ; Orden de puertos: HDMI, DisplayPort, DVI, VGA; otros por codigo.
    for code in [17, 18, 15, 16, 3, 4, 1, 2]
        if HasCode(values, code)
            sorted.Push(code)
    others := ''
    for code in values
        if !HasCode(sorted, code)
            others .= code '`n'
    for raw in StrSplit(Sort(Trim(others, '`n'), 'N'), '`n')
        if raw != ''
            sorted.Push(Integer(raw))
    return sorted
}

DiscoverInputs(monitor, saved, refresh := false) {
    global uiTest, capabilitiesFile, testCodes
    if IsInternalDisplay(monitor)
        return {values: [], message: 'Built-in display: no switchable inputs. Brightness is available below.'}
    if uiTest
        return {values: testCodes, message: 'Simulated inputs for testing.'}
    if IsLg29wk600(monitor)
        return {values: [17, 18, 15], message: 'LG 29WK600: HDMI 1, HDMI 2 and DisplayPort. Method: ' TransportLabel(monitor) '. Connected inputs are selected manually.'}
    cachedValues := []
    for raw in StrSplit(IniRead(capabilitiesFile, monitor.key, 'Codes', ''), ',')
        if RegExMatch(raw, '^\d+$') && Integer(raw) >= 1 && Integer(raw) <= 65535 && !HasCode(cachedValues, Integer(raw))
            cachedValues.Push(Integer(raw))
    if cachedValues.Length && !refresh
        return {values: cachedValues, message: 'Previously detected inputs. Detect again refreshes the list. Input names follow the standard.'}
    Loop 2 {
        values := ParseInputs(ExportValues(monitor.target))
        if values.Length {
            text := ''
            for value in values
                text .= (text = '' ? '' : ',') value
            IniWrite(text, capabilitiesFile, monitor.key, 'Codes')
            return {values: values, message: 'Inputs advertised by the monitor. Names follow the standard; your monitor may use different labels.'}
        }
        Sleep(400)
    }
    values := []
    for raw in StrSplit(IniRead(capabilitiesFile, monitor.key, 'Codes', ''), ',')
        if RegExMatch(raw, '^\d+$') && Integer(raw) >= 1 && Integer(raw) <= 65535 && !HasCode(values, Integer(raw))
            values.Push(Integer(raw))
    cached := values.Length > 0
    for slot, entry in saved
        if !HasCode(values, entry.code)
            values.Push(entry.code)
    return {values: values, message: cached ? 'Could not refresh capabilities; showing previously detected inputs.' : (values.Length ? 'Could not read capabilities; showing saved inputs.' : 'The monitor did not advertise its inputs. Enable DDC/CI and choose Detect again.')}
}

MakeRows(values, saved, supportedOnly := false) {
    for property in ['disabled', 'connected']
        if saved.HasProp(property)
            for code in saved.%property%
                if !supportedOnly && !HasCode(values, code)
                    values.Push(code)
    for slot, entry in saved
        if !supportedOnly && !HasCode(values, entry.code)
            values.Push(entry.code)
    rows := []
    used := Map()
    for code in OrderedCodes(values) {
        row := {name: PortName(code), code: code, slot: 0, shortcut: '', connected: saved.HasProp('connected') && HasCode(saved.connected, code)}
        for slot, entry in saved {
            if entry.code = code && !used.Has(slot) {
                row.slot := slot
                row.name := entry.name
                row.shortcut := entry.HasProp('shortcut') ? entry.shortcut : 'Ctrl|Alt|' slot
                used[slot] := true
                break
            }
        }
        rows.Push(row)
    }
    for row in rows {
        if row.slot || (saved.HasProp('disabled') && HasCode(saved.disabled, row.code))
            continue
        Loop 9 {
            candidateSlot := A_Index
            if !used.Has(candidateSlot) {
                occupied := false
                for existing in rows
                    if existing.shortcut = 'Ctrl|Alt|' candidateSlot
                        occupied := true
                if occupied
                    continue
                row.slot := candidateSlot
                row.shortcut := 'Ctrl|Alt|' candidateSlot
                used[candidateSlot] := true
                break
            }
        }
    }
    return rows
}

AssignChord(rows, index, chord) {
    if chord != ''
        chord := ValidateChord(chord)
    previous := rows[index].shortcut
    if chord != ''
        for otherIndex, row in rows
            if otherIndex != index && row.shortcut = chord
                row.shortcut := previous
    rows[index].shortcut := chord
}

RowsToAssignments(rows) {
    saved := Map()
    saved.disabled := []
    saved.connected := []
    for row in rows {
        if row.connected
            saved.connected.Push(row.code)
        if row.shortcut != ''
            saved[saved.Count + 1] := {name: row.name, code: row.code, shortcut: row.shortcut}
        else
            saved.disabled.Push(row.code)
    }
    return saved
}

ApplyDarkWindow(window) {
    value := Buffer(4, 0)
    NumPut('Int', 1, value)
    for attribute in [19, 20]
        try DllCall('dwmapi\DwmSetWindowAttribute', 'Ptr', window.Hwnd,
            'UInt', attribute, 'Ptr', value, 'UInt', 4)
    try {
        for hwnd in WinGetControlsHwnd('ahk_id ' window.Hwnd)
            DllCall('uxtheme\SetWindowTheme', 'Ptr', hwnd, 'Str', 'DarkMode_Explorer', 'Ptr', 0)
    }
}

ShowSmooth(window, options) {
    global uiTest
    if uiTest {
        window.Show(options)
        ApplyDarkWindow(window)
        return
    }
    window.Show('Hide ' options)
    ApplyDarkWindow(window)
    try {
        target := 'ahk_id ' window.Hwnd
        WinSetTransparent(0, target)
        window.Show(options)
        for alpha in [32, 64, 96, 128, 160, 192, 224, 255] {
            WinSetTransparent(alpha, target)
            Sleep(12)
        }
    } catch {
        try WinSetTransparent(255, 'ahk_id ' window.Hwnd)
        window.Show(options)
    }
}

CloseSmooth(window) {
    global uiTest
    if !uiTest {
        try {
            target := 'ahk_id ' window.Hwnd
            for alpha in [224, 192, 160, 128, 96, 64, 32, 0] {
                WinSetTransparent(alpha, target)
                Sleep(12)
            }
        }
        window.Hide()
    }
    window.Destroy()
}

EnableDarkTheme() {
    ; Windows 10/11 uxtheme: request dark common controls before creating GUIs.
    try DllCall('uxtheme\#135', 'Int', 2, 'Int')
}

ShowAssignments(*) {
    global selectedMonitor, availableMonitors, monitorProfiles, wizardOpen, busy, globalShortcuts
    if wizardOpen || busy
        return
    try RefreshAvailableMonitors()
    catch as err {
        MsgBox(err.Message, 'Shortcuts', 'Iconx')
        return
    }
    labels := []
    for index, monitor in availableMonitors
        labels.Push(MonitorLabel(monitor) (availableMonitors.Length > 1 ? '  /  Display ' index : ''))
    window := Gui(, 'SwitchMonitor Shortcuts')
    window.BackColor := '1E1E1E'
    window.SetFont('s10 cD4D4D4', 'Segoe UI')
    window.AddText('x40 y27 w460 h38 Center cFFFFFF', 'Shortcuts').SetFont('s22 Bold')
    window.AddText('x40 y82 w460 h21 Center cB7B7B7', 'Select a monitor to view or edit its shortcuts.').SetFont('s9')
    window.AddText('x110 y130 w70 h25', 'Monitor:').SetFont('s10 Bold')
    choice := window.AddDropDownList('x188 y125 w240 Choose' FindMonitorIndex(selectedMonitor.key), labels)
    choice.Enabled := availableMonitors.Length > 1
    if availableMonitors.Length = 1 {
        choice.Visible := false
        window.AddText('x188 y125 w240 h30 Background2D2D2D cFFFFFF +0x200', '  ' labels[1])
    }
    window.AddText('x40 y169 w460 h1 Background3C3C3C')
    view := {rows: [], height: 510}
    footerLine := window.AddText('x40 y350 w460 h1 Background3C3C3C')
    footer := window.AddText('x40 y372 w460 h42 Center cB7B7B7', '')
    closeButton := SolidButton(window, 'x198 y435 w144 h38', 'Close', '3C3C3C')
    closeButton.OnEvent('Click', (*) => CloseSmooth(window))
    choice.OnEvent('Change', ChangeSelection)
    window.OnEvent('Close', (*) => CloseSmooth(window))
    window.OnEvent('Escape', (*) => CloseSmooth(window))
    RenderAssignments()
    RoundControls([{control: closeButton, width: 144, height: 38}])
    ShowSmooth(window, 'w540 h' view.height)
    if uiTest {
        if choice.Value != FindMonitorIndex(selectedMonitor.key)
            throw Error('Shortcuts did not open with the selected monitor.')
        CloseSmooth(window)
        return
    }

    ChangeSelection(*) {
        SelectActiveMonitor(choice.Value)
        RenderAssignments()
        window.Show('h' view.height)
        ApplyDarkWindow(window)
    }
    RenderAssignments() {
        monitor := availableMonitors[choice.Value]
        for control in view.rows
            control.Visible := false
        view.rows := []
        entries := []
        if monitorProfiles.Has(monitor.key)
            for slot, entry in monitorProfiles[monitor.key].assignments
                entries.Push(entry)
        if !entries.Length
            entries.Push({name: 'No shortcuts saved', shortcut: ''})
        for index, entry in entries {
            y := 190 + (index - 1) * 50
            label := window.AddText('x60 y' y ' w190 h34 cFFFFFF +0x200', entry.name)
            button := SolidButton(window, 'x258 y' y ' w222 h34',
                !entry.HasOwnProp('code') ? 'Open Settings'
                    : (entry.shortcut = '' ? 'Switch to ' entry.name : ShortcutLabel(entry.shortcut)), '2D2D2D')
            if entry.HasOwnProp('code')
                button.OnEvent('Click', SwitchFromShortcuts.Bind(monitor, entry))
            else
                button.OnEvent('Click', EditAssignments)
            view.rows.Push(label)
            view.rows.Push(button)
            RoundControls([{control: button, width: 222, height: 34}])
        }
        footerY := 195 + entries.Length * 50
        footerLine.Move(, footerY)
        footer.Move(, footerY + 22)
        footer.Value := 'Next input: ' ShortcutLabel(globalShortcuts['cycle']) '    Settings: ' ShortcutLabel(globalShortcuts['settings'])
        closeButton.Move(, footerY + 86)
        view.height := footerY + 142
    }
    EditAssignments(*) {
        CloseSmooth(window)
        OpenLearning()
    }
    SwitchFromShortcuts(monitor, entry, *) {
        SendInputCommand(monitor, entry)
    }
}

CheckForUpdates(*) {
    CheckForUpdatesCore(false)
}

CheckForUpdatesSilent(*) {
    CheckForUpdatesCore(true)
}

CheckForUpdatesCore(silent) {
    global APP_VERSION, availableUpdate, updateNoticeActive, notificationTestActive
    api := 'https://api.github.com/repos/greypaulino/SwitchMonitor/releases/latest'
    try {
        request := ComObject('WinHttp.WinHttpRequest.5.1')
        request.SetTimeouts(5000, 5000, 5000, 5000)
        request.Open('GET', api, false)
        request.SetRequestHeader('Accept', 'application/vnd.github+json')
        request.SetRequestHeader('User-Agent', 'SwitchMonitor/' APP_VERSION)
        request.Send()
        if request.Status = 404 {
            if !silent
                MsgBox('No published releases are available yet.', 'SwitchMonitor', 'Iconi')
            return
        }
        if request.Status != 200
            throw Error('GitHub returned HTTP ' request.Status '.')
        if !RegExMatch(request.ResponseText, '"tag_name"\s*:\s*"v?([0-9]+(?:\.[0-9]+){1,3})"', &match)
            throw Error('The latest release has no supported version tag.')
        latest := match[1]
        if IsNewerVersion(latest, APP_VERSION) {
            assetPattern := '"browser_download_url"\s*:\s*"(https://github\.com/greypaulino/SwitchMonitor/releases/download/[^"/]+/SwitchMonitor-Setup-' StrReplace(latest, '.', '\.') '\.exe)"'
            assetUrl := RegExMatch(request.ResponseText, assetPattern, &asset) ? asset[1] : ''
            hashUrl := RegExMatch(request.ResponseText, '"browser_download_url"\s*:\s*"(https://github\.com/greypaulino/SwitchMonitor/releases/download/[^"/]+/SHA256\.json)"', &hashAsset) ? hashAsset[1] : ''
            if assetUrl = '' || hashUrl = ''
                throw Error('The release is missing its installer or SHA256 manifest.')
            manifest := ComObject('WinHttp.WinHttpRequest.5.1')
            manifest.SetTimeouts(5000, 5000, 5000, 5000)
            manifest.Open('GET', hashUrl, false)
            manifest.SetRequestHeader('User-Agent', 'SwitchMonitor/' APP_VERSION)
            manifest.Send()
            if manifest.Status != 200
                throw Error('Could not read the installer checksum.')
            namePattern := '"File"\s*:\s*"SwitchMonitor-Setup-' StrReplace(latest, '.', '\.') '\.exe"\s*,\s*"Hash"\s*:\s*"([0-9A-Fa-f]{64})"'
            if !RegExMatch(manifest.ResponseText, namePattern, &checksum)
                throw Error('The release has no valid installer checksum.')
            firstNotice := availableUpdate.version != latest
            availableUpdate := {version: latest, url: assetUrl, hash: StrUpper(checksum[1])}
            BuildTrayMenu()
            if firstNotice || !silent {
                notificationTestActive := false
                updateNoticeActive := true
                TrayTip('SwitchMonitor ' latest ' is available. Click here to update.', 'SwitchMonitor update')
            }
        } else if !silent
            MsgBox('SwitchMonitor is up to date (version ' APP_VERSION ').', 'SwitchMonitor', 'Iconi')
    } catch as err {
        if !silent
            MsgBox('Could not check for updates: ' err.Message, 'SwitchMonitor', 'Iconx')
    }
}

ShowTestNotification(*) {
    global notificationTestActive, updateNoticeActive
    updateNoticeActive := false
    notificationTestActive := true
    TrayTip('Click this notification to confirm that activation works.',
        'SwitchMonitor notification test')
}

ShowPendingUpdateCompletion() {
    global APP_VERSION
    path := AppPath('update-complete.ini')
    if !FileExist(path)
        return
    version := IniRead(path, 'Update', 'Version', '')
    FileDelete(path)
    if version = APP_VERSION
        SetTimer(ShowUpdateCompletePopup.Bind(version), -700)
}

ShowUpdateCompletePopup(version, *) {
    window := Gui('+AlwaysOnTop -Caption +ToolWindow', 'SwitchMonitor update')
    window.BackColor := '1E1E1E'
    window.SetFont('s11 cFFFFFF', 'Segoe UI')
    window.AddText('x20 y16 w320 h28 Center', 'SwitchMonitor ' version ' installed successfully.')
    MonitorGetWorkArea(MonitorGetPrimary(), &left, &top, &right, &bottom)
    window.Show('NoActivate x' (right - 376) ' y' (bottom - 92) ' w356 h64')
    ApplyDarkWindow(window)
    SetTimer(() => window.Destroy(), -2000)
}

UpdateNotificationClicked(wParam, lParam, msg, hwnd) {
    global availableUpdate, updateNoticeActive, notificationTestActive
    ; NOTIFYICON_VERSION_4 puts the event in LOWORD(lParam).
    if hwnd != A_ScriptHwnd || (lParam & 0xFFFF) != 1029
        return
    if notificationTestActive {
        notificationTestActive := false
        MsgBox('Notification click received successfully.', 'SwitchMonitor', 'Iconi')
        return
    }
    if !updateNoticeActive || availableUpdate.version = ''
        return
    updateNoticeActive := false
    SetTimer(InstallAvailableUpdate, -100)
}

CanAutoInstallUpdate() {
    return !FileExist(A_ScriptDir '\portable.flag')
        && (FileExist(A_ScriptDir '\installed.flag') || A_IsCompiled)
}

InstallAvailableUpdate(*) {
    global availableUpdate, updateNoticeActive
    updateNoticeActive := false
    update := availableUpdate
    if update.version = ''
        return
    if !CanAutoInstallUpdate() {
        MsgBox('Automatic installation is available from the installed edition. Download this release from GitHub to update this portable or source copy.', 'SwitchMonitor update', 'Iconi')
        Run('https://github.com/greypaulino/SwitchMonitor/releases/latest')
        return
    }
    directory := AppPath('updates')
    DirCreate(directory)
    target := directory '\SwitchMonitor-Setup-' update.version '.exe'
    partial := target '.' DllCall('GetCurrentProcessId') '.part'
    try {
        TrayTip('Downloading SwitchMonitor ' update.version '...', 'SwitchMonitor update')
        if FileExist(target)
            FileDelete(target)
        Download(update.url, partial)
        file := FileOpen(partial, 'r')
        valid := file.Length > 100000 && file.ReadUShort() = 0x5A4D
        file.Close()
        if !valid
            throw Error('The downloaded file is not a valid installer.')
        FileMove(partial, target)
        helper := A_ScriptDir '\Update-Helper.ps1'
        if !FileExist(helper)
            throw Error('The update helper is missing.')
        TrayTip('Download complete. Installing SwitchMonitor ' update.version '...', 'SwitchMonitor update')
        command := '"' A_WinDir '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' helper '" -Installer "' target '" -ExpectedHash ' update.hash ' -ProcessId ' DllCall('GetCurrentProcessId') ' -AppDir "' A_ScriptDir '"'
        Run(command, , 'Hide')
        Sleep(250)
        ExitApp()
    } catch as err {
        if FileExist(partial)
            FileDelete(partial)
        MsgBox('Could not install the update: ' err.Message, 'SwitchMonitor update', 'Iconx')
    }
}

IsNewerVersion(candidate, current) {
    newer := StrSplit(candidate, '.')
    installed := StrSplit(current, '.')
    Loop 4 {
        a := A_Index <= newer.Length ? Integer(newer[A_Index]) : 0
        b := A_Index <= installed.Length ? Integer(installed[A_Index]) : 0
        if a != b
            return a > b
    }
    return false
}

ShowAbout(*) {
    global globalShortcuts
    window := Gui(, 'About SwitchMonitor')
    window.BackColor := '1E1E1E'
    window.SetFont('s10 cD4D4D4', 'Segoe UI')
    window.AddText('x24 y22 w448 h39 Center cFFFFFF', 'SwitchMonitor').SetFont('s23 Bold')
    window.AddText('x24 y69 w448 h23 Center cB7B7B7', 'Version ' APP_VERSION)
    window.AddText('x24 y111 w448 h70 Center', 'Switch monitor inputs with keyboard shortcuts. Each monitor keeps its own settings, and shortcut conflicts are prevented.')
    window.AddText('x24 y198 w448 h51 Center cB7B7B7', 'Data: ' AppPath() '`nSettings: ' ShortcutLabel(globalShortcuts['settings']) '  |  Next input: ' ShortcutLabel(globalShortcuts['cycle'])).SetFont('s9')
    testButton := SolidButton(window, 'x88 y270 w150 h35', 'Test notification', '3C3C3C')
    testButton.OnEvent('Click', ShowTestNotification)
    closeButton := SolidButton(window, 'x258 y270 w150 h35', 'Close', '3C3C3C')
    closeButton.OnEvent('Click', (*) => CloseSmooth(window))
    window.OnEvent('Close', (*) => CloseSmooth(window))
    window.OnEvent('Escape', (*) => CloseSmooth(window))
    RoundControls([{control: testButton, width: 150, height: 35},
        {control: closeButton, width: 150, height: 35}])
    ShowSmooth(window, 'w496 h328')
}

SelfTest() {
    global assignments, selectedMonitor, settingsFile, uiTest, availableMonitors, testCodes, monitorProfiles, registeredBindings, returnWatches
    global brightnessValues, brightnessSelectedKey, brightnessLinked, brightnessPanel, brightnessPending
    global availableUpdate, inputReturnFile, inputReturnHistory

    if !IsNewerVersion('1.3.0', '1.2.0') || IsNewerVersion('1.2.0', '1.2.0')
        || IsNewerVersion('1.1.9', '1.2.0') || !IsNewerVersion('1.2.1', '1.2.0')
        throw Error('Update version comparison failed.')
    if BrightnessStep(0) != 1 || BrightnessStep(299) != 1 || BrightnessStep(300) != 5
        || BrightnessStep(499) != 5 || BrightnessStep(500) != 10
        || BrightnessClamp(-5) != 0 || BrightnessClamp(105) != 100
        throw Error('Brightness repeat steps or limits failed.')
    if BrightnessSteppedTarget(1, 1, 5) != 5
        || BrightnessSteppedTarget(5, 1, 5) != 10
        || BrightnessSteppedTarget(3, 1, 10) != 10
        || BrightnessSteppedTarget(10, 1, 10) != 20
        || BrightnessSteppedTarget(19, -1, 5) != 15
        || BrightnessSteppedTarget(10, -1, 10) != 0
        throw Error('Brightness steps did not align with five- and ten-point marks.')
    firstTap := BrightnessTapPlan(0, 1000, 0, 1, 0)
    secondTap := BrightnessTapPlan(1000, 1100, 1, 1, firstTap.streak)
    thirdTap := BrightnessTapPlan(1100, 1200, 1, 1, secondTap.streak)
    fourthTap := BrightnessTapPlan(1200, 1300, 1, 1, thirdTap.streak)
    slowerTap := BrightnessTapPlan(1300, 1500, 1, 1, fourthTap.streak)
    if firstTap.step != 1 || secondTap.step != 1 || thirdTap.step != 5
        || fourthTap.step != 5 || slowerTap.step != 1
        || BrightnessTapPlan(1300, 1400, 1, -1, fourthTap.streak).step != 1
        throw Error('Rapid brightness taps did not start at the third press or reset on slower/reversed presses.')
    if BrightnessValueAtX(12, 7, 343, 9) != 0
        || BrightnessValueAtX(175, 7, 343, 9) != 50
        || BrightnessValueAtX(339, 7, 343, 9) != 100
        throw Error('Brightness slider click positions did not match the trackbar channel.')
    if BrightnessToPercent(25, 50) != 50 || BrightnessToRaw(50, 50) != 25
        || BrightnessToPercent(50, 50) != 100 || BrightnessToRaw(100, 50) != 50
        || BrightnessToRaw(25, 255) != 64
        throw Error('Brightness normalization did not respect the monitor maximum.')
    if BrightnessParseRange('10,Brightness,Read+Write,25,50,').max != 50
        throw Error('The monitor-reported brightness maximum was not parsed.')
    for chord in ['Ctrl|Alt|NumpadSub', 'Ctrl|Alt|NumpadAdd', 'Ctrl|Alt|NumpadMult']
        if ValidateChord(chord) != chord
            throw Error('A brightness shortcut is invalid: ' chord)
    startupTestPath := A_Temp '\SwitchMonitor-startup-test-' DllCall('GetCurrentProcessId') '.lnk'
    try {
        ConfigureStartup(true, startupTestPath)
        FileGetShortcut(startupTestPath, &shortcutTarget, , &shortcutArgs)
        if shortcutTarget != (A_IsCompiled ? A_ScriptFullPath : A_AhkPath)
            || !InStr(shortcutArgs, '--activate')
            throw Error('Startup shortcut points to the wrong program.')
        ConfigureStartup(false, startupTestPath)
        if FileExist(startupTestPath)
            throw Error('Startup shortcut was not removed.')
    } finally {
        if FileExist(startupTestPath)
            FileDelete(startupTestPath)
    }
    fixture := 'Monitor Device Name: "\\.\DISPLAY1\Monitor0"`nMonitor Name: "Mismo modelo"`nSerial Number: ""`nMonitor ID: "MONITOR\TEST\0001"`n`nMonitor Device Name: "\\.\DISPLAY2\Monitor0"`nMonitor Name: "Mismo modelo"`nSerial Number: ""`nMonitor ID: "MONITOR\TEST\0002"`n'
    monitors := ParseMonitors(fixture, false)
    if MonitorListChanged(monitors, monitors) || !MonitorListChanged(monitors, [monitors[1]])
        || !MonitorListChanged(monitors, [monitors[2], monitors[1]])
        throw Error('Monitor topology comparison failed.')
    originalReturnFile := inputReturnFile
    inputReturnFile := A_Temp '\SwitchMonitor-input-return-test-'
        DllCall('GetCurrentProcessId') '.ini'
    try {
        availableMonitors := monitors
        RememberInputSwitch(monitors[1], 17, 15)
        inputReturnHistory.Clear()
        LoadInputReturnHistory()
        if !inputReturnHistory.Has(monitors[1].key)
            || inputReturnHistory[monitors[1].key].origin != 17
            || inputReturnHistory[monitors[1].key].destination != 15
            throw Error('The previous input was not restored after restart.')
        ForgetInputSwitch(monitors[1])
        if inputReturnHistory.Has(monitors[1].key)
            || IniRead(inputReturnFile, monitors[1].key, 'Switch', '') != ''
            throw Error('The previous input was not removed after returning.')
    } finally {
        if FileExist(inputReturnFile)
            FileDelete(inputReturnFile)
        inputReturnFile := originalReturnFile
        inputReturnHistory.Clear()
    }
    if BrightnessDirectDisplayName(monitors[1]) != '\\.\DISPLAY1'
        || BrightnessDirectDisplayName(monitors[2]) != '\\.\DISPLAY2'
        throw Error('Brightness monitor names did not map to Windows displays.')
    backupSource := A_Temp '\SwitchMonitor-settings-source-' DllCall('GetCurrentProcessId') '.ini'
    backupTarget := A_Temp '\SwitchMonitor-settings-backup-' DllCall('GetCurrentProcessId') '.ini'
    try {
        IniWrite('Ctrl|Alt|F9', backupSource, 'GlobalShortcuts', 'brightnessUp')
        SaveSettingsBackup(backupSource, backupTarget)
        if !IsSettingsBackup(backupTarget)
            || IniRead(backupTarget, 'GlobalShortcuts', 'brightnessUp', '') != 'Ctrl|Alt|F9'
            throw Error('Settings backup did not preserve shortcuts.')
    } finally {
        if FileExist(backupSource)
            FileDelete(backupSource)
        if FileExist(backupTarget)
            FileDelete(backupTarget)
    }
    watch := ReturnSyncState(monitors[1], 17)
    if watch.Observe(17) != 'wait' || watch.Observe(17) != 'wait'
        throw Error('La sincronizacion se inicio sin una perdida de conexion.')
    if watch.Observe(0) != 'wait' || watch.Observe(17) != 'sync'
        throw Error('No se detecto un retorno estable a HDMI 1.')
    watch := ReturnSyncState(monitors[1], 17)
    watch.Observe(0)
    if watch.Observe(18) != 'cancel'
        throw Error('La sincronizacion intentaria sobrescribir otra entrada.')
    watch := ReturnSyncState(monitors[1], 17)
    watch.Observe(0), watch.Observe(17), watch.Observe(0)
    if watch.Observe(17) != 'sync'
        throw Error('The return was not detected after control was restored.')
    if monitors.Length != 2 || monitors[1].key = monitors[2].key
        throw Error('Fallo de identificacion de monitores del mismo modelo.')
    returnWatches[monitors[1].key] := ReturnSyncState(monitors[1], 17)
    returnWatches[monitors[2].key] := ReturnSyncState(monitors[2], 18)
    CancelReturnSync(monitors[1].key)
    if returnWatches.Count != 1 || !returnWatches.Has(monitors[2].key)
        throw Error('La espera de retorno de un monitor cancelo la del otro.')
    CancelReturnSync()
    availableMonitors := monitors
    selectedMonitor := monitors[2]
    BuildTrayMenu()
    if DllCall('user32\GetMenuItemCount', 'Ptr', A_TrayMenu.Handle, 'Int') != 12
        throw Error('Menu de bandeja con varios monitores incorrecto.')
    availableMonitors := [monitors[1]]
    BuildTrayMenu()
    if DllCall('user32\GetMenuItemCount', 'Ptr', A_TrayMenu.Handle, 'Int') != 11
        throw Error('Menu de bandeja con un monitor incorrecto.')
    availableUpdate := {version: '9.9.9', url: 'https://example.invalid/update.exe', hash: ''}
    BuildTrayMenu()
    if DllCall('user32\GetMenuItemCount', 'Ptr', A_TrayMenu.Handle, 'Int') != 11
        throw Error('The update action is missing from the tray menu.')
    availableUpdate := {version: '', url: '', hash: ''}
    BuildTrayMenu()
    availableMonitors := monitors
    profiles := Map()
    profiles[monitors[1].key] := {monitor: monitors[1], rows: [], assignments: Map(1,
        {name: 'HDMI 1', code: 17, shortcut: 'Ctrl|Alt|1'})}
    profiles[monitors[2].key] := {monitor: monitors[2], rows: [], assignments: Map(1,
        {name: 'HDMI 2', code: 18, shortcut: 'Ctrl|Alt|1'})}
    rejected := false
    try ValidateProfileShortcuts(profiles)
    catch
        rejected := true
    if !rejected
        throw Error('Se acepto un atajo duplicado en otro monitor.')
    profiles[monitors[2].key].assignments[1].shortcut := 'Ctrl|Alt|9'
    ValidateProfileShortcuts(profiles)
    profiles[monitors[1].key].assignments[1].shortcut := 'Ctrl|Alt|Shift|8'
    rejected := false
    try ValidateGlobalShortcuts(profiles, Map('brightnessNext', 'Ctrl|Alt|NumpadMult'))
    catch
        rejected := true
    if !rejected
        throw Error('The main-keyboard brightness shortcut alias was not reserved.')
    profiles[monitors[1].key].assignments[1].shortcut := 'Ctrl|Alt|1'
    globalTest := Map('cycle', 'Ctrl|Alt|M', 'settings', 'Ctrl|Alt|Shift|M')
    ValidateGlobalShortcuts(profiles, globalTest)
    globalTest['cycle'] := 'Ctrl|Alt|9'
    rejected := false
    try ValidateGlobalShortcuts(profiles, globalTest)
    catch
        rejected := true
    if !rejected
        throw Error('A global shortcut conflicted with a monitor shortcut.')
    globalTest['cycle'] := 'Ctrl|Alt|M'
    globalTest['settings'] := 'Ctrl|Alt|M'
    rejected := false
    try ValidateGlobalShortcuts(profiles, globalTest)
    catch
        rejected := true
    if !rejected
        throw Error('Duplicate global shortcuts were accepted.')
    monitorProfiles := profiles
    RegisterShortcuts()
    if !registeredBindings.Length
        throw Error('No se registraron los atajos de los dos monitores.')
    lg := BuildMonitor(Map('Monitor Device Name', '\\.\DISPLAY7\Monitor0', 'Monitor Name', 'LG HDR WFHD',
        'Adapter Name', 'Intel(R) HD Graphics 3000', 'Monitor ID', 'MONITOR\GSM7714\TEST'))
    if !UsesIntelLg(lg) || UsesIntelLg(monitors[1]) || lg.device != '\\.\DISPLAY7\Monitor0'
        throw Error('Fallo al elegir el transporte del monitor seleccionado.')
    lgSaved := Map(1, {code: 17, name: 'HDMI 1'}, 4, {code: 16, name: 'DP inexistente'})
    lgSaved.disabled := [16]
    lgSaved.connected := [17, 15, 16]
    lgRows := MakeRows([17, 18, 15], lgSaved, true)
    if lgRows.Length != 3 || lgRows[3].code != 15 || FindNextConnected(lgRows, 17).code != 15
        throw Error('El perfil LG incorporo entradas que no existen o rompio el ciclo.')
    amdLg := BuildMonitor(Map('Monitor Device Name', 'AMD-DISPLAY', 'Monitor Name', 'Generic PnP Monitor',
        'Adapter Name', 'Radeon 7', 'Monitor ID', 'MONITOR\GSM7715\TEST'))
    if !IsLg29wk600(amdLg) || !UsesAmdLg(amdLg)
        || MakeRows(DiscoverInputs(amdLg, lgSaved).values, lgSaved, true).Length != 3
        throw Error('The AMD LG variant exposed a nonexistent DisplayPort 2 input.')
    laptop := BuildMonitor(Map('Monitor Device Name', 'LAPTOP-DISPLAY', 'Monitor Name', 'N/A',
        'Short Monitor ID', 'BOE1234', 'Monitor ID', 'MONITOR\BOE1234\TEST'))
    laptop.internalBrightness := 'DISPLAY\BOE1234\TEST_0'
    laptop.laptopModel := 'Inspiron N5050'
    if MonitorLabel(laptop) != 'N5050 Laptop Screen' || DiscoverInputs(laptop, lgSaved).values.Length
        || MakeRows([], lgSaved, IsInternalDisplay(laptop)).Length
        throw Error('A built-in display exposed monitor input shortcuts.')
    if ValidateChord('K|RControl|LAlt|LShift') != 'Ctrl|Alt|Shift|K' || ValidateChord('B|A') != 'A|B'
        throw Error('Fallo normalizando combinaciones de dos y cuatro teclas.')
    for invalid in ['K', 'Ctrl|Alt|Shift|Win|K'] {
        rejected := false
        try {
            ValidateChord(invalid)
        } catch {
            rejected := true
        }
        if !rejected
            throw Error('Se acepto una combinacion fuera de los limites: ' invalid)
    }
    path := A_Temp '\switchMonitor-selftest-' DllCall('GetCurrentProcessId') '.ini'
    try {
        first := Map(1, {name: 'HDMI 1', code: 17}, 3, {name: 'DP', code: 15})
        second := Map(2, {name: 'Otro HDMI', code: 16, shortcut: 'Ctrl|Alt|9'})
        SaveAssignments(path, monitors[1], first)
        SaveAssignments(path, monitors[2], second)
        loaded := LoadAssignments(path, monitors[1].key)
        if loaded.Count != 2 || loaded[1].code != 17 || loaded[3].name != 'DP'
            throw Error('Fallo al recuperar las asignaciones.')
        SaveAssignments(path, monitors[1], Map(3, {name: 'DP renombrado', code: 15}))
        loaded := LoadAssignments(path, monitors[1].key)
        other := LoadAssignments(path, monitors[2].key)
        if loaded.Has(1) || loaded[3].name != 'DP renombrado' || other[2].code != 16
            throw Error('Fallo al editar o aislar perfiles.')
        assignments := loaded
        RegisterShortcuts()
        assignments := Map(1, {name: 'Prueba cuatro teclas', code: 17, shortcut: 'Ctrl|Alt|Shift|K'}, 2, {name: 'Prueba dos teclas', code: 15, shortcut: 'A|B'})
        RegisterShortcuts()
        assignments := loaded
        RegisterShortcuts()
        selectedMonitor := monitors[1]
        settingsFile := path
        uiTest := true
        availableMonitors := monitors
        brightnessSelectedKey := monitors[1].key
        brightnessLinked := true
        BrightnessApply(monitors[1], 42)
        if brightnessValues.Get(monitors[1].key, -1) != 42
            || brightnessValues.Get(monitors[2].key, -1) != 42
            throw Error('Linked brightness did not update both monitors.')
        monitors[2].testActive := false
        if BrightnessActiveMonitors().Length != 1
            || BrightnessEffectiveLinked()
            throw Error('An inactive second display was included in brightness linking.')
        ShowBrightnessPanel()
        offRow := brightnessPanel.rows[monitors[2].key]
        if offRow.number.Value != 'OFF' || offRow.slider.Visible
            || offRow.rowHeight >= brightnessPanel.rows[monitors[1].key].rowHeight
            throw Error('The inactive display was not rendered as a compact OFF row: '
                offRow.number.Value ', visible=' offRow.slider.Visible ', height=' offRow.rowHeight)
        NextBrightnessMonitor()
        if brightnessSelectedKey != monitors[1].key
            throw Error('The keyboard selected an inactive display.')
        ToggleBrightnessLink()
        if !brightnessLinked
            throw Error('An inactive display changed the saved chain preference.')
        BrightnessDestroyPanel()
        monitors[2].testActive := true
        ShowBrightnessPanel()
        if !IsObject(brightnessPanel) || !brightnessPanel.linkedView
            || brightnessPanel.rows[monitors[2].key].slider.Visible
            throw Error('Linked brightness did not show a single slider.')
        panelRegion := DllCall('gdi32\CreateRectRgn', 'Int', 0, 'Int', 0,
            'Int', 0, 'Int', 0, 'Ptr')
        try {
            if !DllCall('user32\GetWindowRgn', 'Ptr', brightnessPanel.window.Hwnd,
                'Ptr', panelRegion, 'Int')
                || DllCall('gdi32\PtInRegion', 'Ptr', panelRegion,
                    'Int', 0, 'Int', 0, 'Int')
                || !DllCall('gdi32\PtInRegion', 'Ptr', panelRegion,
                    'Int', 0, 'Int', brightnessPanel.height - 1, 'Int')
                throw Error('The brightness panel did not round only its upper corners.')
        } finally DllCall('gdi32\DeleteObject', 'Ptr', panelRegion)
        brightnessPanel.rows[monitors[1].key].label.GetPos(, &firstNameY, , &firstNameHeight)
        brightnessPanel.rows[monitors[1].key].number.GetPos(, &firstPercentY, , &firstPercentHeight)
        brightnessPanel.rows[monitors[1].key].mainButton.GetPos(, &firstMainY)
        brightnessPanel.rows[monitors[2].key].label.GetPos(, &secondNameY)
        brightnessPanel.rows[monitors[2].key].mainButton.GetPos(, &secondMainY)
        brightnessPanel.link.GetPos(, &linkY)
        if firstNameY != firstPercentY || firstNameY != firstMainY
            || firstNameY != linkY || secondNameY != secondMainY
            || firstNameHeight != firstPercentHeight
            || brightnessPanel.height != 128
            throw Error('Linked brightness header and main-display icons are misaligned.')
        if brightnessPanel.highlightKey != monitors[1].key
            throw Error('Linked brightness did not show the shared highlight.')
        brightnessPanel.rows[monitors[2].key].label.GetPos(, &lowerNameY, , &lowerNameHeight)
        if lowerNameY + lowerNameHeight >= brightnessPanel.rows[monitors[1].key].slider.y
            throw Error('Linked monitor names overlap the brightness slider.')
        ToggleBrightnessPanel()
        if IsObject(brightnessPanel)
            throw Error('Brightness tray toggle did not close the visible panel.')
        ToggleBrightnessPanel()
        if !IsObject(brightnessPanel) || !brightnessPanel.linkedView
            throw Error('Brightness tray toggle did not reopen the panel.')
        brightnessLinked := false
        BrightnessRefreshPanel()
        if brightnessPanel.linkedView || !brightnessPanel.rows[monitors[2].key].slider.Visible
            throw Error('Independent brightness did not restore both sliders.')
        brightnessPanel.rows[monitors[1].key].label.GetPos(, &separateNameY, , &separateNameHeight)
        brightnessPanel.rows[monitors[1].key].number.GetPos(, &separatePercentY, , &separatePercentHeight)
        brightnessPanel.rows[monitors[1].key].mainButton.GetPos(, &separateMainY)
        brightnessPanel.settings.GetPos(, &separateSettingsY)
        if separateNameY != separatePercentY || separateNameY != separateMainY
            || separateNameY != separateSettingsY
            || separateNameHeight != separatePercentHeight
            throw Error('Independent brightness header is misaligned.')
        if brightnessPanel.highlightKey != monitors[1].key
            throw Error('Brightness highlight did not select the first monitor.')
        NextBrightnessMonitor()
        if brightnessSelectedKey != monitors[2].key
            || brightnessPanel.highlightKey != monitors[2].key
            throw Error('Brightness highlight did not follow the next monitor.')
        BrightnessApply(monitors[1], 27)
        BrightnessApply(monitors[2], 53)
        BrightnessPanelStarAction(true)
        if !brightnessLinked || !brightnessPanel.linkedView
            throw Error('Holding star did not link monitor brightness.')
        if brightnessValues[monitors[1].key] != 53
            || brightnessValues[monitors[2].key] != 53
            || brightnessPanel.rows[monitors[1].key].slider.Value != 53
            throw Error('Linking brightness did not select and apply the highest slider value.')
        BrightnessPanelStarAction(true)
        if brightnessLinked || brightnessPanel.linkedView
            throw Error('Holding star did not unlink monitor brightness.')
        BrightnessApply(monitors[1], 68)
        BrightnessApply(monitors[2], 42)
        BrightnessPanelStarAction(true)
        if !brightnessLinked || brightnessValues[monitors[2].key] != 68
            || brightnessPanel.rows[monitors[1].key].slider.Value != 68
            throw Error('Linking brightness did not keep the first monitor higher value.')
        BrightnessPanelStarAction(true)
        BrightnessApply(monitors[1], 42)
        BrightnessApply(monitors[2], 42)
        BrightnessPanelStarAction(false)
        if brightnessSelectedKey != monitors[1].key
            throw Error('Tapping star did not select the next monitor.')
        BrightnessPanelStarAction(false)
        if brightnessSelectedKey != monitors[2].key
            throw Error('A second star tap did not cycle to the second monitor.')
        firstSlider := brightnessPanel.rows[monitors[1].key].slider
        secondSlider := brightnessPanel.rows[monitors[2].key].slider
        BrightnessWheelAt(brightnessPanel, brightnessPanel.x + 30,
            brightnessPanel.y + 160, 1)
        if firstSlider.Value != 42 || secondSlider.Value != 43
            throw Error('Mouse wheel did not adjust the monitor under the pointer.')
        firstSlider.Value := 43
        BrightnessSliderChanged(monitors[1].key, firstSlider)
        secondSlider.Value := 44
        BrightnessSliderChanged(monitors[2].key, secondSlider)
        SetTimer(BrightnessFlushSlider, 0)
        if brightnessPending.Count != 2
            throw Error('A second slider replaced the first pending adjustment.')
        BrightnessFlushSlider()
        if brightnessValues[monitors[1].key] != 43 || brightnessValues[monitors[2].key] != 44
            throw Error('The two brightness sliders did not update independently.')
        BrightnessAdjust(1)
        if brightnessValues[monitors[2].key] != 45 || secondSlider.Value != 45
            || brightnessPending.Get(monitors[2].key, -1) != 45
            throw Error('Brightness shortcut did not update the slider before the hardware write.')
        BrightnessFlushSlider()
        BrightnessQueueValue(monitors[2].key, 55, 'immediate')
        if secondSlider.Value != 55 || secondSlider._display != 55
            throw Error('Rapid keyboard brightness did not move immediately.')
        BrightnessQueueValue(monitors[2].key, 65, 'animated')
        if secondSlider.Value != 65 || secondSlider._display >= 65
            throw Error('Sustained keyboard brightness did not animate its ten-point jump.')
        BrightnessQueueValue(monitors[2].key, 45, 'immediate')
        BrightnessFlushSlider()
        BrightnessPanelDigit('7')
        BrightnessPanelDigit('5')
        if brightnessPanel.rows[monitors[2].key].number.Value != 'Listening'
            || brightnessValues[monitors[2].key] != 45
            throw Error('Typed brightness changed the slider before confirmation.')
        BrightnessPanelEscape()
        if brightnessPanel.numericActive
            || brightnessPanel.rows[monitors[2].key].number.Value != '45%'
            throw Error('Escape did not cancel typed brightness.')
        BrightnessPanelDigit('1')
        BrightnessPanelDigit('0')
        BrightnessPanelDigit('0')
        BrightnessPanelEnter()
        if brightnessPanel.numericActive || brightnessValues[monitors[2].key] != 100
            || secondSlider.Value != 100
            throw Error('Enter did not apply typed brightness of 100%.')
        BrightnessFlushSlider()
        BrightnessPanelDigit('6')
        BrightnessPanelDigit('0')
        Sleep(2250)
        if brightnessPanel.numericActive || brightnessValues[monitors[2].key] != 60
            throw Error('Typed brightness did not apply after two seconds.')
        BrightnessFlushSlider()
        BrightnessPanelEnter()
        if IsObject(brightnessPanel)
            throw Error('Enter did not close the panel outside numeric entry.')
        ShowBrightnessPanel()
        BrightnessPanelEscape()
        if IsObject(brightnessPanel)
            throw Error('Escape did not close the panel outside numeric entry.')
        availableMonitors := [monitors[1]]
        ShowBrightnessPanel()
        if brightnessPanel.highlightKey != ''
            throw Error('A single monitor should not show a selection highlight.')
        HideBrightnessPanel()
        availableMonitors := monitors
        testCodes := ParseInputs('VCP Code`tVCP Code Name`tRead-Write`tCurrent Value`tMaximum Value`tPossible Values`n60`tInput Select`tRead+Write`t17`t18`t17, 18, 15, 16, 17')
        if testCodes.Length != 4
            throw Error('Fallo al leer las entradas anunciadas.')
        OpenLearning()
        if IniRead(path, 'GlobalShortcuts', 'brightnessUp', '') != 'Ctrl|Alt|F9'
            || IniRead(path, 'Brightness', 'Linked', '') != '1'
            throw Error('Brightness Settings did not save its shortcut or link state.')
        learned := LoadAssignments(path, monitors[1].key)
        learnedOther := LoadAssignments(path, monitors[2].key)
        foundSecond := false
            for slot, entry in learnedOther
                if entry.code = 16 && entry.shortcut = 'Ctrl|Alt|9'
                    foundSecond := true
        if !foundSecond
            throw Error('No se guardo el borrador del segundo monitor.')
        SelectActiveMonitor(2, false)
        ShowAssignments()
        SelectActiveMonitor(1, false)
        if learned.Count != 4 || learned[1].shortcut != 'Ctrl|Alt|K' || learned[3].shortcut != 'Ctrl|Alt|1'
            throw Error('Fallo al guardar desde el asistente.')
        if !HasCode(learned.connected, 17) || !HasCode(learned.connected, 15) || HasCode(learned.connected, 18)
            throw Error('No se guardaron las marcas de conexion.')
        cycle := MakeRows(testCodes, learned)
        if FindNextConnected(cycle, 17).code != 15 || FindNextConnected(cycle, 15).code != 17 || FindNextConnected(cycle, 18).code != 15
            throw Error('El ciclo no salta puertos desconectados o no vuelve al principio.')
        history := {origin: 17, destination: 15, tick: A_TickCount}
        if FindReturnInput(cycle, 15, history).code != 17
            || IsObject(FindReturnInput(cycle, 17, history))
            throw Error('The previous input was not limited to the matching destination.')
        for row in cycle
            if row.code = 18
                row.connected := true
        history := {origin: 18, destination: 15, tick: A_TickCount}
        if ChooseCycleInput(cycle, 15, history, history.tick + 9000).entry.code != 18
            || ChooseCycleInput(cycle, 15, history, history.tick + 10001).entry.code != 17
            throw Error('The quick return did not expire before the normal input cycle.')
        for row in cycle
            if row.code = 18
                row.connected := false
        history := {origin: 17, destination: 15, tick: A_TickCount}
        for row in cycle
            if row.code = 17
                row.connected := false
        if IsObject(FindReturnInput(cycle, 15, history))
            throw Error('The previous input must remain marked as connected.')
        for row in cycle
            if row.code = 17
                row.connected := true
        for row in cycle
            row.connected := row.code = 17
        if FindNextConnected(cycle, 17).code != 17
            throw Error('Fallo del ciclo con un solo puerto conectado.')
        for row in cycle
            row.connected := false
        rejected := false
        try {
            FindNextConnected(cycle, 17)
        } catch {
            rejected := true
        }
        if !rejected
            throw Error('Se acepto un ciclo sin puertos conectados.')
        disabledRows := MakeRows(testCodes, learned)
        AssignChord(disabledRows, 4, '')
        SaveAssignments(path, monitors[1], RowsToAssignments(disabledRows))
        reloadedRows := MakeRows(testCodes, LoadAssignments(path, monitors[1].key))
        if reloadedRows[4].shortcut != '' || reloadedRows[1].shortcut != 'Ctrl|Alt|K'
            throw Error('No se conservo la opcion Sin atajo.')
        availableMonitors := [monitors[1]]
        OpenLearning()
        if IniRead(path, 'Brightness', 'Linked', '') != '1'
            throw Error('Single-monitor Settings changed the saved link preference.')
        FileAppend('PASS: input cycle, profiles, multi-monitor shortcuts and shortcut capture.`n', AppPath('monitor-selftest-result.txt'), 'UTF-8')
    } finally {
        if FileExist(path)
            FileDelete(path)
    }
}



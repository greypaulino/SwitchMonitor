#Requires AutoHotkey v2.0
#SingleInstance Force
#Include intelLegacyDdc.ahk
#Include amdLgDdc.ahk
#Include returnSyncState.ahk
#Include settingsUi.ahk
;@Ahk2Exe-SetName SwitchMonitor
;@Ahk2Exe-SetDescription SwitchMonitor - monitor input shortcuts
;@Ahk2Exe-SetVersion 1.3.0.0
;@Ahk2Exe-SetOrigFilename SwitchMonitor.exe

APP_VERSION := '1.3.0'

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
globalShortcuts := Map('cycle', 'Ctrl|Alt|M', 'settings', 'Ctrl|Alt|Shift|M')
registeredGlobalHotkeys := []
busy := false
wizardOpen := false
uiTest := false
capabilitiesFile := AppPath('monitorCapabilities.ini')
availableMonitors := []
testCodes := []
registeredBindings := []
buttonStyles := Map()
buttonHoverHwnd := 0
cycleEntries := []
wizardCycle := 0
returnWatches := Map()
returnWatchEnabled := false

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
    LoadGlobalShortcuts()
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
    if !wizardOpen && (!A_Args.Length || A_Args[1] != '--activate' || !monitorProfiles.Count)
        OpenLearning()
    SetTimer(CheckForUpdatesSilent, -5000)
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
    return IsLg29wk600(monitor) ? 'LG 29WK600' : monitor.name
}

LoadMonitorProfiles() {
    global availableMonitors, monitorProfiles, settingsFile
    monitorProfiles := Map()
    for monitor in availableMonitors {
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
    global availableMonitors, selectedMonitor, globalShortcuts
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
    for kind, fallback in Map('cycle', 'Ctrl|Alt|M', 'settings', 'Ctrl|Alt|Shift|M') {
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
    for kind, action in Map('cycle', NextConnected, 'settings', OpenLearning) {
        if globalShortcuts[kind] = ''
            continue
        name := AhkChord(globalShortcuts[kind])
        Hotkey(name, action, 'On')
        registeredGlobalHotkeys.Push(name)
    }
}

ValidateGlobalShortcuts(profiles, shortcuts) {
    if shortcuts['cycle'] != '' && shortcuts['cycle'] = shortcuts['settings']
        throw Error('Next input and Settings cannot use the same shortcut.')
    for key, profile in profiles
        for slot, entry in profile.assignments
            for kind, chord in shortcuts
                if chord != '' && entry.shortcut = chord
                    throw Error(ShortcutLabel(chord) ' is already assigned to ' MonitorLabel(profile.monitor) ' / ' entry.name '.')
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

ParseMonitors(data) {
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
    return monitors
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
    return InStr(monitor.id, 'GSM7714') > 0
}

UsesIntelLg(monitor) {
    return IsLg29wk600(monitor) && monitor.HasProp('adapter') && InStr(monitor.adapter, 'Intel')
}

UsesAmdLg(monitor) {
    return IsLg29wk600(monitor) && monitor.HasProp('adapter') && RegExMatch(monitor.adapter, 'i)AMD|ATI|Radeon')
}

CompatibilityInfo(monitor) {
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
    return UsesIntelLg(monitor) ? 'Intel CUI direct / LG F4 (source 0x50)'
        : UsesAmdLg(monitor) ? 'AMD ADL2 / LG F4 (source 0x50), experimental'
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
            if UsesIntelLg(monitor) {
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
    compatibility := CompatibilityInfo(monitor)
    if !compatibility.supported
        return {current: 0, message: compatibility.message}
    if UsesAmdLg(monitor) {
        try api := AmdLgDdc(monitor.device, monitor.name)
        catch as err
            return {current: 0, message: err.Message}
    }
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

RefreshAvailableMonitors() {
    global availableMonitors, selectedMonitor, uiTest, profileError
    if uiTest
        return
    currentKey := IsObject(selectedMonitor) ? selectedMonitor.key : ''
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
    brush := DllCall('gdi32\CreateSolidBrush', 'UInt', GdiColor(style.hovered ? style.hover : style.fill), 'Ptr')
    oldBrush := DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', brush, 'Ptr')
    oldPen := DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', DllCall('gdi32\GetStockObject', 'Int', 8, 'Ptr'), 'Ptr')
    DllCall('gdi32\RoundRect', 'Ptr', dc, 'Int', left, 'Int', top, 'Int', right, 'Int', bottom, 'Int', 24, 'Int', 24)
    DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', oldPen)
    DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', oldBrush)
    DllCall('gdi32\DeleteObject', 'Ptr', brush)
    DllCall('gdi32\SetBkMode', 'Ptr', dc, 'Int', 1)
    disabled := NumGet(lParam, 16, 'UInt') & 4
    DllCall('gdi32\SetTextColor', 'Ptr', dc, 'UInt', disabled ? 0xAAAAAA : 0xFFFFFF)
    font := SendMessage(0x31, 0, 0, hwnd)
    oldFont := font ? DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', font, 'Ptr') : 0
    DllCall('user32\DrawTextW', 'Ptr', dc, 'Str', style.control.Text, 'Int', -1,
        'Ptr', lParam + 40, 'UInt', 0x8025)
    if oldFont
        DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', oldFont)
    return true
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

SendInputCommand(monitor, entry, knownBefore := -1, showMessage := true) {
    global monitorTool, busy
    if busy
        return
    busy := true
    try {
        compatibility := CompatibilityInfo(monitor)
        if !compatibility.supported
            throw Error(compatibility.message)
        if UsesIntelLg(monitor) || UsesAmdLg(monitor) {
            previous := UsesIntelLg(monitor) ? (knownBefore >= 0 ? knownBefore : ReadMonitorInput(monitor)) : 0
            api := UsesIntelLg(monitor) ? IntelLegacyDdc(monitor.device) : AmdLgDdc(monitor.device, monitor.name)
            api.SendLgInput(entry.code)
            ArmReturnSync(monitor, previous, entry.code)
            FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' target=' monitor.target ' transport=' TransportLabel(monitor) ' requested='
                entry.code ' lg-value=' Format('{:02X}', IntelLegacyDdc.LgValue(entry.code)) ' accepted=1 physical=unverified`n',
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
            detail := IsLg29wk600(monitor) ? '`n`nWhen another computer is shown, the LG may stop responding on this connection. Return to this computer with the monitor joystick. You can export diagnostics from Settings.' : ''
            MsgBox(err.Message detail, 'Switch input', 'Iconx')
        }
        return false
    } finally {
        busy := false
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

CycleConnected(monitor, rows) {
    global busy
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
        if !current
            throw Error(result.message '`n`nNext input needs the current input. No switch was sent.')
        next := FindNextConnected(rows, current)
        if next.code = current {
            ToolTip('No other input is marked as connected.')
            SetTimer(() => ToolTip(), -3000)
            return
        }
        busy := false
        SendInputCommand(monitor, next, current)
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
    window.AddText('x40 y20 w460 h38 Center cFFFFFF', 'Shortcuts').SetFont('s22 Bold')
    window.AddText('x40 y69 w460 h21 Center cB7B7B7', 'Select a monitor to view or edit its shortcuts.').SetFont('s9')
    window.AddText('x110 y108 w70 h25', 'Monitor:').SetFont('s10 Bold')
    choice := window.AddDropDownList('x188 y103 w240 Choose' FindMonitorIndex(selectedMonitor.key), labels)
    choice.Enabled := availableMonitors.Length > 1
    if availableMonitors.Length = 1 {
        choice.Visible := false
        window.AddText('x188 y103 w240 h30 Background2D2D2D cFFFFFF +0x200', '  ' labels[1])
    }
    window.AddText('x40 y145 w460 h1 Background3C3C3C')
    view := {rows: [], height: 480}
    footerLine := window.AddText('x40 y320 w460 h1 Background3C3C3C')
    footer := window.AddText('x40 y340 w460 h42 Center cB7B7B7', '')
    closeButton := SolidButton(window, 'x198 y395 w144 h38', 'Close', '3C3C3C')
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
            y := 164 + (index - 1) * 46
            label := window.AddText('x60 y' y ' w190 h34 cFFFFFF +0x200', entry.name)
            button := SolidButton(window, 'x258 y' y ' w222 h34',
                entry.shortcut = '' ? 'Open Settings' : ShortcutLabel(entry.shortcut), '2D2D2D')
            button.OnEvent('Click', EditAssignments)
            view.rows.Push(label)
            view.rows.Push(button)
            RoundControls([{control: button, width: 222, height: 34}])
        }
        footerY := 172 + entries.Length * 46
        footerLine.Move(, footerY)
        footer.Move(, footerY + 18)
        footer.Value := 'Next input: ' ShortcutLabel(globalShortcuts['cycle']) '    Settings: ' ShortcutLabel(globalShortcuts['settings'])
        closeButton.Move(, footerY + 76)
        view.height := footerY + 130
    }
    EditAssignments(*) {
        CloseSmooth(window)
        OpenLearning()
    }
}

CheckForUpdates(*) {
    CheckForUpdatesCore(false)
}

CheckForUpdatesSilent(*) {
    CheckForUpdatesCore(true)
}

CheckForUpdatesCore(silent) {
    global APP_VERSION
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
            ShowUpdateNotice(latest, assetUrl)
        } else if !silent
            MsgBox('SwitchMonitor is up to date (version ' APP_VERSION ').', 'SwitchMonitor', 'Iconi')
    } catch as err {
        if !silent
            MsgBox('Could not check for updates: ' err.Message, 'SwitchMonitor', 'Iconx')
    }
}

ShowUpdateNotice(version, assetUrl) {
    global updateNotice
    if IsSet(updateNotice) && IsObject(updateNotice)
        try updateNotice.Destroy()
    notice := Gui('+AlwaysOnTop -Caption +ToolWindow', 'SwitchMonitor update')
    updateNotice := notice
    notice.BackColor := '1E1E1E'
    notice.SetFont('s10 cFFFFFF', 'Segoe UI')
    notice.AddText('x20 y16 w330 h24 cFFFFFF', 'SwitchMonitor ' version ' is available').SetFont('s12 Bold')
    notice.AddText('x20 y46 w330 h37 cB7B7B7', assetUrl != ''
        ? 'Download the new installer to your Downloads folder.'
        : 'The release is ready to view on GitHub.')
    action := SolidButton(notice, 'x20 y92 w235 h35', assetUrl != '' ? 'Download update' : 'View release', '0E639C')
    action.OnEvent('Click', (*) => assetUrl != '' ? DownloadUpdate(version, assetUrl, notice)
        : OpenUpdateRelease(notice))
    dismiss := SolidButton(notice, 'x265 y92 w85 h35', 'Later', '3C3C3C')
    dismiss.OnEvent('Click', (*) => notice.Destroy())
    RoundControls([{control: action, width: 235, height: 35}, {control: dismiss, width: 85, height: 35}])
    notice.OnEvent('Close', (*) => notice.Destroy())
    MonitorGetWorkArea(MonitorGetPrimary(), &left, &top, &right, &bottom)
    notice.Show('NoActivate x' (right - 370) ' y' (bottom - 155) ' w370 h145')
}

OpenUpdateRelease(notice) {
    notice.Destroy()
    Run('https://github.com/greypaulino/SwitchMonitor/releases/latest')
}

DownloadUpdate(version, url, notice) {
    notice.Destroy()
    directory := EnvGet('USERPROFILE') '\Downloads'
    DirCreate(directory)
    target := directory '\SwitchMonitor-Setup-' version '.exe'
    partial := target '.' DllCall('GetCurrentProcessId') '.part'
    try {
        if !FileExist(target) {
            Download(url, partial)
            file := FileOpen(partial, 'r')
            valid := file.Length > 100000 && file.ReadUShort() = 0x5A4D
            file.Close()
            if !valid
                throw Error('The downloaded file is not a valid installer.')
            FileMove(partial, target)
        }
        MsgBox('The installer is ready in Downloads:`n' target, 'SwitchMonitor update', 'Iconi')
    } catch as err {
        if FileExist(partial)
            FileDelete(partial)
        MsgBox('Could not download the installer: ' err.Message, 'SwitchMonitor update', 'Iconx')
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
    closeButton := SolidButton(window, 'x176 y270 w144 h35', 'Close', '3C3C3C')
    closeButton.OnEvent('Click', (*) => CloseSmooth(window))
    window.OnEvent('Close', (*) => CloseSmooth(window))
    window.OnEvent('Escape', (*) => CloseSmooth(window))
    RoundControls([{control: closeButton, width: 144, height: 35}])
    ShowSmooth(window, 'w496 h328')
}

SelfTest() {
    global assignments, selectedMonitor, settingsFile, uiTest, availableMonitors, testCodes, monitorProfiles, registeredBindings, returnWatches

    if !IsNewerVersion('1.3.0', '1.2.0') || IsNewerVersion('1.2.0', '1.2.0')
        || IsNewerVersion('1.1.9', '1.2.0') || !IsNewerVersion('1.2.1', '1.2.0')
        throw Error('Update version comparison failed.')
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
    monitors := ParseMonitors(fixture)
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
    if DllCall('user32\GetMenuItemCount', 'Ptr', A_TrayMenu.Handle, 'Int') != 10
        throw Error('Menu de bandeja con varios monitores incorrecto.')
    availableMonitors := [monitors[1]]
    BuildTrayMenu()
    if DllCall('user32\GetMenuItemCount', 'Ptr', A_TrayMenu.Handle, 'Int') != 9
        throw Error('Menu de bandeja con un monitor incorrecto.')
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
        testCodes := ParseInputs('VCP Code`tVCP Code Name`tRead-Write`tCurrent Value`tMaximum Value`tPossible Values`n60`tInput Select`tRead+Write`t17`t18`t17, 18, 15, 16, 17')
        if testCodes.Length != 4
            throw Error('Fallo al leer las entradas anunciadas.')
        OpenLearning()
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
        FileAppend('PASS: input cycle, profiles, multi-monitor shortcuts and shortcut capture.`n', AppPath('monitor-selftest-result.txt'), 'UTF-8')
    } finally {
        if FileExist(path)
            FileDelete(path)
    }
}



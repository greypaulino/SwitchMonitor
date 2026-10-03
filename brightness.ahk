; VCP 0x10 brightness controls and the separate sun icon/slider panel.

InitBrightness() {
    global availableMonitors, brightnessSelectedKey, sunIconHandle, sunIconData
    if availableMonitors.Length
        brightnessSelectedKey := availableMonitors[availableMonitors.Length].key
    iconPath := A_ScriptDir '\brightness-sun.ico'
    if !FileExist(iconPath)
        return
    try {
        sunIconHandle := DllCall('user32\LoadImageW', 'Ptr', 0, 'Str', iconPath,
            'UInt', 1, 'Int', 0, 'Int', 0, 'UInt', 0x10, 'Ptr')
        if !sunIconHandle
            throw Error('Could not load the brightness tray icon.')
        sunIconData := Buffer(976, 0)
        NumPut('UInt', sunIconData.Size, sunIconData, 0)
        NumPut('Ptr', A_ScriptHwnd, sunIconData, 8)
        NumPut('UInt', 2, sunIconData, 16)
        NumPut('UInt', 7, sunIconData, 20)
        NumPut('UInt', 0x8071, sunIconData, 24)
        NumPut('Ptr', sunIconHandle, sunIconData, 32)
        StrPut('Brightness control', sunIconData.Ptr + 40, 128, 'UTF-16')
        if !DllCall('shell32\Shell_NotifyIconW', 'UInt', 0, 'Ptr', sunIconData, 'Int')
            throw Error('Windows rejected the brightness tray icon.')
        OnMessage(0x8071, BrightnessTrayMessage)
        OnMessage(0x201, BrightnessSliderMouseDown)
        OnMessage(0x200, BrightnessBarMouseMove)
        OnMessage(0x202, BrightnessBarMouseUp)
        OnMessage(0x20A, BrightnessPanelMouseWheel)
        OnMessage(0x14, BrightnessSuppressErase)
        OnMessage(0xF, BrightnessPaint)
        OnExit(BrightnessCleanup)
        BrightnessStartRangeJobs()
    } catch as err {
        FileAppend('Brightness tray: ' err.Message '`n', AppPath('monitor-switch.log'), 'UTF-8')
    }
}

BrightnessCleanup(*) {
    global sunIconData, sunIconHandle
    if IsSet(sunIconData) && IsObject(sunIconData)
        DllCall('shell32\Shell_NotifyIconW', 'UInt', 2, 'Ptr', sunIconData)
    if sunIconHandle
        DllCall('user32\DestroyIcon', 'Ptr', sunIconHandle)
}

BrightnessTrayMessage(wParam, lParam, *) {
    if lParam = 0x202 || lParam = 0x205
        ToggleBrightnessPanel()
}

ToggleBrightnessPanel(*) {
    global brightnessPanel
    if !IsObject(brightnessPanel) || brightnessPanel.target = 0
        ShowBrightnessPanel()
    else
        HideBrightnessPanel()
}

BrightnessRead(monitor) {
    global monitorTool, uiTest, brightnessValues
    if uiTest
        return brightnessValues.Get(monitor.key, 50)
    if IsInternalDisplay(monitor)
        return InternalBrightnessRead(monitor)
    range := BrightnessRange(monitor)
    if range.fresh {
        range.fresh := false
        value := range.current
    } else {
        value := UsesIntelLg(monitor)
            ? IntelLegacyDdc(monitor.device, false).GetVcp(0x10)
            : RunWait('"' monitorTool '" /GetValue "' monitor.target '" 10', , 'Hide')
    }
    if value < 0 || value > range.max
        throw Error('Brightness reading is unavailable for ' MonitorLabel(monitor) '.')
    return BrightnessToPercent(value, range.max)
}

BrightnessWrite(monitor, value) {
    global monitorTool, uiTest
    value := BrightnessClamp(value)
    if uiTest
        return
    if IsInternalDisplay(monitor) {
        InternalBrightnessWrite(monitor, value)
        return
    }
    raw := BrightnessToRaw(value, BrightnessRange(monitor).max)
    if UsesIntelLg(monitor)
        IntelLegacyDdc(monitor.device, false).SetVcp(0x10, raw)
    else if RunWait('"' monitorTool '" /SetValue "' monitor.target '" 10 ' raw, , 'Hide') != 0
        throw Error('Brightness command failed for ' MonitorLabel(monitor) '.')
}

BrightnessRange(monitor) {
    global brightnessRanges, brightnessRangeJobs, monitorTool, settingsFile, uiTest
    if brightnessRanges.Has(monitor.key)
        return brightnessRanges[monitor.key]
    if uiTest {
        brightnessRanges[monitor.key] := {max: 100, current: 50, fresh: false}
        return brightnessRanges[monitor.key]
    }
    if IsInternalDisplay(monitor) {
        brightnessRanges[monitor.key] := {max: 100, current: InternalBrightnessRead(monitor), fresh: false}
        return brightnessRanges[monitor.key]
    }
    saved := IniRead(settingsFile, 'BrightnessRanges', monitor.key, '')
    if RegExMatch(saved, '^\d+$') && Integer(saved) >= 1 && Integer(saved) <= 65535 {
        brightnessRanges[monitor.key] := {max: Integer(saved), current: -1, fresh: false}
        return brightnessRanges[monitor.key]
    }
    if brightnessRangeJobs.Has(monitor.key)
        throw Error('Detecting the brightness range for ' MonitorLabel(monitor) '.')
    path := A_Temp '\SwitchMonitor-brightness-' DllCall('GetCurrentProcessId') '-' A_TickCount '.csv'
    try {
        RunWait('"' monitorTool '" /scomma "' path '" "' monitor.target '"', , 'Hide')
        if !FileExist(path)
            throw Error('ControlMyMonitor did not export the brightness range.')
        range := BrightnessParseRange(FileRead(path))
        brightnessRanges[monitor.key] := range
        IniWrite(range.max, settingsFile, 'BrightnessRanges', monitor.key)
        return range
    } finally {
        if FileExist(path)
            FileDelete(path)
    }
}

BrightnessParseRange(data) {
    for line in StrSplit(data, '`n', '`r') {
        if !RegExMatch(line, '^"?10"?,')
            continue
        fields := StrSplit(line, ',')
        if fields.Length < 5 || !RegExMatch(Trim(fields[4], '" '), '^\d+$')
            || !RegExMatch(Trim(fields[5], '" '), '^\d+$')
            break
        current := Integer(Trim(fields[4], '" '))
        maximum := Integer(Trim(fields[5], '" '))
        if maximum >= 1 && maximum <= 65535 && current >= 0 && current <= maximum
            return {max: maximum, current: current, fresh: true}
        break
    }
    throw Error('VCP 0x10 did not report a valid current and maximum value.')
}

BrightnessStartRangeJobs() {
    global availableMonitors, brightnessRangeJobs, monitorTool, settingsFile, uiTest
    if uiTest
        return
    for index, monitor in availableMonitors {
        if IsInternalDisplay(monitor)
            continue
        saved := IniRead(settingsFile, 'BrightnessRanges', monitor.key, '')
        if RegExMatch(saved, '^\d+$') && Integer(saved) >= 1 && Integer(saved) <= 65535
            continue
        path := A_Temp '\SwitchMonitor-brightness-' DllCall('GetCurrentProcessId') '-' index '.csv'
        try {
            Run('"' monitorTool '" /scomma "' path '" "' monitor.target '"', , 'Hide', &pid)
            brightnessRangeJobs[monitor.key] := {path: path, pid: pid, started: A_TickCount}
        } catch as err {
            FileAppend('Brightness range: ' err.Message '`n', AppPath('monitor-switch.log'), 'UTF-8')
        }
    }
    if brightnessRangeJobs.Count
        SetTimer(BrightnessPollRangeJobs, 250)
}

InternalBrightnessRead(monitor) {
    service := ComObjGet('winmgmts:\\.\root\wmi')
    for item in service.ExecQuery('SELECT Active, InstanceName, CurrentBrightness FROM WmiMonitorBrightness')
        if item.Active && item.InstanceName = monitor.internalBrightness
            return BrightnessClamp(item.CurrentBrightness)
    throw Error('Windows did not report brightness for ' MonitorLabel(monitor) '.')
}

InternalBrightnessWrite(monitor, value) {
    service := ComObjGet('winmgmts:\\.\root\wmi')
    for item in service.ExecQuery('SELECT Active, InstanceName FROM WmiMonitorBrightnessMethods') {
        if !item.Active || item.InstanceName != monitor.internalBrightness
            continue
        ; SWbemServices exposes this method's result as an empty string in AHK.
        ; COM raises an exception if the call itself fails.
        item.WmiSetBrightness(0, BrightnessClamp(value))
        return
    }
    throw Error('Windows did not provide brightness control for ' MonitorLabel(monitor) '.')
}

BrightnessPollRangeJobs(*) {
    global brightnessRangeJobs, brightnessRanges, brightnessValues, brightnessPanel, brightnessLastActivity, settingsFile
    completed := []
    for key, job in brightnessRangeJobs {
        if ProcessExist(job.pid)
            continue
        completed.Push(key)
        try {
            range := BrightnessParseRange(FileRead(job.path))
            brightnessRanges[key] := range
            IniWrite(range.max, settingsFile, 'BrightnessRanges', key)
            if !brightnessValues.Has(key) || brightnessLastActivity < job.started
                brightnessValues[key] := BrightnessToPercent(range.current, range.max)
            range.fresh := false
            if IsObject(brightnessPanel) && brightnessPanel.rows.Has(key) {
                row := brightnessPanel.rows[key]
                row.slider.Enabled := true
                row.slider.SetImmediate(brightnessValues[key])
                BrightnessRefreshPanel()
            }
        } catch as err {
            FileAppend('Brightness range: ' err.Message '`n', AppPath('monitor-switch.log'), 'UTF-8')
        } finally {
            if FileExist(job.path)
                FileDelete(job.path)
        }
    }
    for key in completed
        brightnessRangeJobs.Delete(key)
    if !brightnessRangeJobs.Count
        SetTimer(BrightnessPollRangeJobs, 0)
}

BrightnessToPercent(raw, maximum) {
    return BrightnessClamp(100 * raw / maximum)
}

BrightnessToRaw(percent, maximum) {
    return Max(0, Min(maximum, Round(BrightnessClamp(percent) * maximum / 100)))
}

BrightnessMonitor(key) {
    global availableMonitors
    for monitor in availableMonitors
        if monitor.key = key
            return monitor
    return 0
}

BrightnessActiveMonitor() {
    global brightnessSelectedKey, selectedMonitor, availableMonitors
    monitor := BrightnessMonitor(brightnessSelectedKey)
    if IsObject(monitor)
        return monitor
    monitor := IsObject(selectedMonitor) ? selectedMonitor : availableMonitors[1]
    brightnessSelectedKey := monitor.key
    return monitor
}

BrightnessApply(monitor, value) {
    global brightnessLinked, availableMonitors, brightnessValues, brightnessLastActivity, brightnessPanel
    value := BrightnessClamp(value)
    failures := []
    targets := brightnessLinked ? availableMonitors : [monitor]
    for target in targets {
        try {
            BrightnessWrite(target, value)
            brightnessValues[target.key] := value
        } catch as err {
            failures.Push(MonitorLabel(target) ': ' err.Message)
            FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' brightness ERROR=' err.Message '`n',
                AppPath('monitor-switch.log'), 'UTF-8')
        }
    }
    brightnessLastActivity := A_TickCount
    BrightnessRefreshPanel()
    if IsObject(brightnessPanel) {
        brightnessPanel.status.Value := failures.Length ? failures[1] : ''
        BrightnessInvalidateStatus(brightnessPanel)
    }
    return !failures.Length
}

BrightnessDown(*) {
    BrightnessHotkey(-1, 'brightnessDown')
}

BrightnessUp(*) {
    BrightnessHotkey(1, 'brightnessUp')
}

BrightnessHotkey(direction, kind) {
    global brightnessHotkeyActive, brightnessCaptureActive, globalShortcuts
    if brightnessCaptureActive || brightnessHotkeyActive
        return
    FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' brightness-hotkey=' A_ThisHotkey
        ' direction=' direction '`n', AppPath('monitor-switch.log'), 'UTF-8')
    chord := globalShortcuts[kind]
    if chord = ''
        return
    keys := StrSplit(chord, '|')
    trigger := keys[keys.Length]
    if A_ThisHotkey = '^!-'
        trigger := '-'
    else if A_ThisHotkey = '^!+='
        trigger := '='
    brightnessHotkeyActive := true
    try {
        started := BrightnessKeyboardStart(direction)
        nextTick := started + 300
        while GetKeyState(trigger, 'P') {
            now := A_TickCount
            if now >= nextTick {
                duration := now - started
                sustained := duration >= 500
                BrightnessAdjust(direction, BrightnessStep(duration), sustained)
                nextTick := now + (sustained ? 230 : 200)
            }
            Sleep(20)
        }
    } catch as err {
        FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' brightness-hotkey ERROR=' err.Message '`n',
            AppPath('monitor-switch.log'), 'UTF-8')
        ToolTip('Brightness: ' err.Message)
        SetTimer(() => ToolTip(), -2500)
    } finally {
        brightnessHotkeyActive := false
    }
}

BrightnessPanelHotkeysActive() {
    global brightnessPanel, brightnessCaptureActive
    return IsObject(brightnessPanel) && brightnessPanel.target = 1
        && !brightnessCaptureActive
}

BrightnessPanelKey(direction, trigger) {
    global brightnessHotkeyActive
    if brightnessHotkeyActive
        return
    brightnessHotkeyActive := true
    try {
        started := BrightnessKeyboardStart(direction)
        nextTick := started + 300
        while GetKeyState(trigger, 'P') && BrightnessPanelHotkeysActive() {
            now := A_TickCount
            if now >= nextTick {
                duration := now - started
                sustained := duration >= 500
                BrightnessAdjust(direction, BrightnessStep(duration), sustained)
                nextTick := now + (sustained ? 230 : 200)
            }
            Sleep(20)
        }
    } catch as err {
        ToolTip('Brightness: ' err.Message)
        SetTimer(() => ToolTip(), -2500)
    } finally {
        brightnessHotkeyActive := false
    }
}

BrightnessPanelNext(trigger) {
    if !BrightnessPanelHotkeysActive()
        return
    released := KeyWait(trigger, 'T1')
    if !BrightnessPanelHotkeysActive()
        return
    BrightnessPanelStarAction(!released)
    if !released
        KeyWait(trigger)
}

BrightnessPanelStarAction(held) {
    if held
        ToggleBrightnessLink()
    else
        NextBrightnessMonitor()
}

BrightnessPanelDigit(digit) {
    global brightnessPanel, brightnessSelectedKey, brightnessLastActivity, availableMonitors
    if !BrightnessPanelHotkeysActive()
        return
    panel := brightnessPanel
    key := panel.numericActive ? panel.numericKey : brightnessSelectedKey
    if !panel.rows.Has(key)
        return
    row := panel.linkedView ? panel.rows[availableMonitors[1].key] : panel.rows[key]
    if !row.slider.Enabled
        return
    candidate := panel.numericActive ? panel.numericInput digit : digit
    if StrLen(candidate) > 3 || candidate + 0 > 100
        return
    firstDigit := !panel.numericActive
    panel.numericActive := true
    panel.numericKey := key
    panel.numericOverlayKey := key
    panel.numericInput := candidate
    panel.numericLastDigit := A_TickCount
    panel.numericSeconds := 2
    row.number.Value := 'Listening'
    if firstDigit
        BrightnessStartTypingFade(panel, 1)
    else
        BrightnessInvalidateTypingArea(panel)
    brightnessLastActivity := A_TickCount
    SetTimer(BrightnessCommitTypedValue, -2000)
    SetTimer(BrightnessTypingCountdown, 80)
}

BrightnessCommitTypedValue(*) {
    global brightnessPanel
    if !IsObject(brightnessPanel) || !brightnessPanel.numericActive
        return false
    panel := brightnessPanel
    key := panel.numericKey
    value := panel.numericInput + 0
    panel.numericActive := false
    panel.numericSeconds := 0
    SetTimer(BrightnessCommitTypedValue, 0)
    SetTimer(BrightnessTypingCountdown, 0)
    BrightnessQueueValue(key, value)
    BrightnessStartTypingFade(panel, 0)
    return true
}

BrightnessCancelTypedValue() {
    global brightnessPanel, brightnessLastActivity
    if !IsObject(brightnessPanel) || !brightnessPanel.numericActive
        return false
    brightnessPanel.numericActive := false
    SetTimer(BrightnessCommitTypedValue, 0)
    SetTimer(BrightnessTypingCountdown, 0)
    brightnessLastActivity := A_TickCount
    BrightnessRefreshPanel()
    BrightnessStartTypingFade(brightnessPanel, 0)
    return true
}

BrightnessTypingCountdown(*) {
    global brightnessPanel
    if !IsObject(brightnessPanel) || !brightnessPanel.numericActive {
        SetTimer(BrightnessTypingCountdown, 0)
        return
    }
    seconds := Max(1, Ceil((2000 - (A_TickCount
        - brightnessPanel.numericLastDigit)) / 1000))
    if seconds != brightnessPanel.numericSeconds {
        brightnessPanel.numericSeconds := seconds
        BrightnessInvalidateTypingArea(brightnessPanel)
    }
}

BrightnessStartTypingFade(panel, target) {
    panel.numericFadeFrom := panel.numericOpacity
    panel.numericFadeTo := target
    panel.numericFadeStarted := A_TickCount
    panel.numericFadeDuration := target ? 160 : 220
    SetTimer(BrightnessAnimateTyping, 15)
    BrightnessAnimateTyping()
}

BrightnessAnimateTyping(*) {
    global brightnessPanel
    if !IsObject(brightnessPanel) {
        SetTimer(BrightnessAnimateTyping, 0)
        return
    }
    panel := brightnessPanel
    elapsed := Min(1, Max(0, (A_TickCount - panel.numericFadeStarted)
        / panel.numericFadeDuration))
    eased := elapsed * elapsed * (3 - 2 * elapsed)
    panel.numericOpacity := panel.numericFadeFrom
        + (panel.numericFadeTo - panel.numericFadeFrom) * eased
    BrightnessInvalidateTypingArea(panel)
    if elapsed < 1
        return
    SetTimer(BrightnessAnimateTyping, 0)
    if panel.numericFadeTo = 0 && !panel.numericActive {
        panel.numericInput := ''
        panel.numericKey := ''
        panel.numericOverlayKey := ''
    }
}

BrightnessTypingBounds(panel) {
    if panel.linkedView
        return {top: 7, bottom: panel.height - 7}
    return BrightnessHighlightBounds(panel, panel.numericOverlayKey)
}

BrightnessInvalidateTypingArea(panel) {
    if !panel.bottom || panel.numericOverlayKey = ''
        return
    area := BrightnessTypingBounds(panel)
    rect := Buffer(16, 0)
    NumPut('Int', 8, rect, 0)
    NumPut('Int', area.top, rect, 4)
    NumPut('Int', 392, rect, 8)
    NumPut('Int', area.bottom, rect, 12)
    DllCall('user32\RedrawWindow', 'Ptr', panel.window.Hwnd,
        'Ptr', rect, 'Ptr', 0, 'UInt', 0x105)
}

BrightnessPanelEnter(*) {
    if !BrightnessCommitTypedValue()
        HideBrightnessPanel()
}

BrightnessPanelEscape(*) {
    if !BrightnessCancelTypedValue()
        HideBrightnessPanel()
}

#HotIf BrightnessPanelHotkeysActive()
NumpadAdd::BrightnessPanelKey(1, 'NumpadAdd')
NumpadSub::BrightnessPanelKey(-1, 'NumpadSub')
NumpadMult::BrightnessPanelNext('NumpadMult')
+=::BrightnessPanelKey(1, '=')
-::BrightnessPanelKey(-1, '-')
+8::BrightnessPanelNext('8')
0::BrightnessPanelDigit('0')
1::BrightnessPanelDigit('1')
2::BrightnessPanelDigit('2')
3::BrightnessPanelDigit('3')
4::BrightnessPanelDigit('4')
5::BrightnessPanelDigit('5')
6::BrightnessPanelDigit('6')
7::BrightnessPanelDigit('7')
8::BrightnessPanelDigit('8')
9::BrightnessPanelDigit('9')
Numpad0::BrightnessPanelDigit('0')
Numpad1::BrightnessPanelDigit('1')
Numpad2::BrightnessPanelDigit('2')
Numpad3::BrightnessPanelDigit('3')
Numpad4::BrightnessPanelDigit('4')
Numpad5::BrightnessPanelDigit('5')
Numpad6::BrightnessPanelDigit('6')
Numpad7::BrightnessPanelDigit('7')
Numpad8::BrightnessPanelDigit('8')
Numpad9::BrightnessPanelDigit('9')
Enter::BrightnessPanelEnter()
NumpadEnter::BrightnessPanelEnter()
Esc::BrightnessPanelEscape()
#HotIf

BrightnessStep(heldMs) {
    return heldMs >= 500 ? 10 : heldMs >= 300 ? 5 : 1
}

BrightnessKeyboardStart(direction) {
    static lastPress := 0, lastDirection := 0, rapidStreak := 0
    now := A_TickCount
    plan := BrightnessTapPlan(lastPress, now, lastDirection, direction, rapidStreak)
    rapidStreak := plan.streak
    lastPress := now
    lastDirection := direction
    BrightnessAdjust(direction, plan.step)
    return now
}

BrightnessTapPlan(previousTick, now, previousDirection, direction, streak) {
    nextStreak := previousTick && now - previousTick < 167
        && previousDirection = direction ? streak + 1 : 0
    return {streak: nextStreak, step: nextStreak >= 2 ? 5 : 1}
}

BrightnessClamp(value) {
    return Max(0, Min(100, Round(value)))
}

BrightnessSteppedTarget(current, direction, step) {
    if step <= 1
        return BrightnessClamp(current + direction)
    if direction > 0
        return BrightnessClamp((Floor(current / step) + 1) * step)
    return BrightnessClamp((Ceil(current / step) - 1) * step)
}

BrightnessAdjust(direction, step := 1, animate := false) {
    global brightnessValues
    monitor := BrightnessActiveMonitor()
    current := brightnessValues.Has(monitor.key) ? brightnessValues[monitor.key] : BrightnessRead(monitor)
    ShowBrightnessPanel()
    BrightnessQueueValue(monitor.key, BrightnessSteppedTarget(current, direction, step),
        animate ? 'animated' : 'immediate')
}

BrightnessPanelMonitorName(monitor) {
    name := MonitorLabel(monitor)
    if IsInternalDisplay(monitor) || RegExMatch(name, 'i)\bMonitor$')
        return name
    return name ' Monitor'
}

BrightnessDirectDisplayName(monitor) {
    prefix := '\\.\DISPLAY'
    if SubStr(monitor.device, 1, StrLen(prefix)) = prefix {
        separator := InStr(monitor.device, '\', false, StrLen(prefix) + 1)
        return separator ? SubStr(monitor.device, 1, separator - 1) : monitor.device
    }
    return ''
}

BrightnessDisplayName(monitor) {
    global availableMonitors
    direct := BrightnessDirectDisplayName(monitor)
    if direct != ''
        return direct
    ; WMI-only built-in panels have no GDI name. Match only an unambiguous
    ; display that is not already represented by another detected monitor.
    if !IsInternalDisplay(monitor)
        return ''
    used := Map()
    for other in availableMonitors {
        if other.key = monitor.key
            continue
        name := BrightnessDirectDisplayName(other)
        if name != ''
            used[StrUpper(name)] := true
    }
    unmatched := []
    Loop MonitorGetCount() {
        name := MonitorGetName(A_Index)
        if !used.Has(StrUpper(name))
            unmatched.Push(name)
    }
    return unmatched.Length = 1 ? unmatched[1] : ''
}

BrightnessMainDisplayName() {
    try {
        return MonitorGetName(MonitorGetPrimary())
    } catch {
        return ''
    }
}

BrightnessCanBeMainDisplay(monitor) {
    if MonitorGetCount() < 2
        return false
    target := BrightnessDisplayName(monitor)
    if target = ''
        return false
    Loop MonitorGetCount() {
        name := MonitorGetName(A_Index)
        if StrUpper(name) != StrUpper(target)
            continue
        mode := Buffer(220, 0)
        NumPut('UShort', mode.Size, mode, 68)
        return !!DllCall('user32\EnumDisplaySettingsExW', 'Str', name,
            'Int', -1, 'Ptr', mode, 'UInt', 0, 'Int')
    }
    return false
}

BrightnessSetMainDisplay(key) {
    global brightnessPanel, brightnessLastActivity
    monitor := BrightnessMonitor(key)
    if !IsObject(monitor)
        return {ok: false, error: 'The selected monitor is unavailable.'}
    targetName := BrightnessDisplayName(monitor)
    if targetName = ''
        return {ok: false, error: 'Windows cannot identify this display.'}
    if StrUpper(targetName) = StrUpper(BrightnessMainDisplayName())
        return {ok: true, error: ''}
    if !BrightnessCanBeMainDisplay(monitor)
        return {ok: false, error: 'This display cannot become the main display in the current mode.'}
    outcome := {ok: true, error: ''}
    try {
        displays := []
        target := 0
        Loop MonitorGetCount() {
            name := MonitorGetName(A_Index)
            mode := Buffer(220, 0)
            NumPut('UShort', mode.Size, mode, 68)
            if !DllCall('user32\EnumDisplaySettingsExW', 'Str', name,
                'Int', -1, 'Ptr', mode, 'UInt', 0, 'Int')
                throw Error('Windows could not read the display layout.')
            display := {name: name, mode: mode,
                x: NumGet(mode, 76, 'Int'), y: NumGet(mode, 80, 'Int')}
            displays.Push(display)
            if StrUpper(name) = StrUpper(targetName)
                target := display
        }
        if !IsObject(target)
            throw Error('The selected display is not active in Windows.')
        for display in displays {
            NumPut('UInt', 0x20, display.mode, 72) ; DM_POSITION
            NumPut('Int', display.x - target.x, display.mode, 76)
            NumPut('Int', display.y - target.y, display.mode, 80)
            flags := 0x10000001 ; CDS_NORESET | CDS_UPDATEREGISTRY
            if display.name = targetName
                flags |= 0x10 ; CDS_SET_PRIMARY
            result := DllCall('user32\ChangeDisplaySettingsExW', 'Str', display.name,
                'Ptr', display.mode, 'Ptr', 0, 'UInt', flags, 'Ptr', 0, 'Int')
            if result != 0
                throw Error('Windows rejected the display change (' result ').')
        }
        result := DllCall('user32\ChangeDisplaySettingsExW', 'Ptr', 0,
            'Ptr', 0, 'Ptr', 0, 'UInt', 0, 'Ptr', 0, 'Int')
        if result != 0
            throw Error('Windows could not apply the display change (' result ').')
        if StrUpper(BrightnessMainDisplayName()) != StrUpper(targetName)
            throw Error('Windows did not confirm the new main display.')
        brightnessLastActivity := A_TickCount
        if IsObject(brightnessPanel) {
            MonitorGetWorkArea(MonitorGetPrimary(), &left, &top, &right, &bottom)
            brightnessPanel.x := Max(left + 8, right - 408)
            brightnessPanel.bottom := bottom
            brightnessPanel.y := Max(top, bottom - brightnessPanel.height)
            brightnessPanel.window.Show('NoActivate x' brightnessPanel.x
                ' y' brightnessPanel.y)
            brightnessPanel.status.Value := ''
            BrightnessUpdateHighlight(brightnessPanel)
        }
    } catch as err {
        outcome := {ok: false, error: err.Message}
        FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss')
            ' main display ERROR=' err.Message '`n', AppPath('monitor-switch.log'), 'UTF-8')
        if IsObject(brightnessPanel) {
            brightnessPanel.status.Value := err.Message
            BrightnessInvalidateStatus(brightnessPanel)
        }
    }
    BrightnessInvalidateMainIcons(brightnessPanel)
    return outcome
}

NextBrightnessMonitor(*) {
    global availableMonitors, brightnessSelectedKey, brightnessLastActivity, globalShortcuts, brightnessCaptureActive
    if brightnessCaptureActive
        return
    if availableMonitors.Length < 2
        return
    BrightnessCancelTypedValue()
    for index, monitor in availableMonitors
        if monitor.key = brightnessSelectedKey {
            brightnessSelectedKey := availableMonitors[Mod(index, availableMonitors.Length) + 1].key
            break
        }
    brightnessLastActivity := A_TickCount
    ShowBrightnessPanel()
    trigger := A_ThisHotkey = '^!+8' ? '8' : 'NumpadMult'
    if A_ThisHotkey != ''
        KeyWait(trigger)
}

SelectBrightnessMonitor(key, *) {
    global brightnessSelectedKey, brightnessLastActivity
    if !IsObject(BrightnessMonitor(key))
        return
    brightnessSelectedKey := key
    brightnessLastActivity := A_TickCount
    BrightnessRefreshPanel()
}

ToggleBrightnessLink(*) {
    global brightnessLinked, settingsFile, brightnessLastActivity, availableMonitors
    global brightnessPanel, brightnessPointerDown
    if availableMonitors.Length < 2
        return
    BrightnessCancelTypedValue()
    BrightnessFlushSlider()
    if !brightnessLinked && !BrightnessMatchLinkedValues()
        return
    brightnessLinked := !brightnessLinked
    IniWrite(brightnessLinked ? '1' : '0', settingsFile, 'Brightness', 'Linked')
    brightnessLastActivity := A_TickCount
    brightnessPointerDown := true
    BrightnessRefreshPanel()
    if IsObject(brightnessPanel) {
        BrightnessBeginTransition(brightnessPanel, 1)
        SetTimer(BrightnessHideCheck, 30)
    }
}

BrightnessMatchLinkedValues() {
    global availableMonitors, brightnessValues, brightnessPanel
    highest := -1
    targets := []
    for monitor in availableMonitors {
        key := monitor.key
        if IsObject(brightnessPanel) && brightnessPanel.rows.Has(key)
            if !brightnessPanel.rows[key].slider.Enabled
                continue
        if !brightnessValues.Has(key) {
            try brightnessValues[key] := BrightnessRead(monitor)
            catch
                continue
        }
        targets.Push(monitor)
        highest := Max(highest, brightnessValues[key])
    }
    if highest < 0
        return false
    for monitor in targets {
        key := monitor.key
        if !brightnessValues.Has(key) || brightnessValues[key] >= highest
            continue
        if !BrightnessApply(monitor, highest)
            return false
    }
    return true
}

ShowBrightnessPanel(*) {
    global brightnessPanel, brightnessLastActivity, brightnessPointerDown
    global availableMonitors, brightnessSelectedKey, brightnessValues, uiTest, buttonStyles
    if IsObject(brightnessPanel) {
        brightnessLastActivity := A_TickCount
        BrightnessRefreshPanel()
        BrightnessBeginTransition(brightnessPanel, 1)
        SetTimer(BrightnessHideCheck, 30)
        return
    }
    if !availableMonitors.Length
        return
    if !IsObject(BrightnessMonitor(brightnessSelectedKey))
        brightnessSelectedKey := availableMonitors[1].key
    window := Gui('+AlwaysOnTop -Caption +ToolWindow', 'Brightness Control')
    window.BackColor := '1E1E1E'
    disableDwmTransition := Buffer(4, 0)
    NumPut('Int', 1, disableDwmTransition)
    DllCall('dwmapi\DwmSetWindowAttribute', 'Ptr', window.Hwnd, 'UInt', 3,
        'Ptr', disableDwmTransition, 'UInt', 4)
    ; Windows 11 can paint a light DWM frame while the clipped window expands.
    ; This panel draws its own edges, so suppress that compositor border.
    darkBorder := Buffer(4, 0)
    NumPut('UInt', 0xFFFFFFFE, darkBorder)
    DllCall('dwmapi\DwmSetWindowAttribute', 'Ptr', window.Hwnd, 'UInt', 34,
        'Ptr', darkBorder, 'UInt', 4)
    window.SetFont('s10 cFFFFFF', 'Segoe UI')
    linkButton := SolidButton(window, 'x325 y12 w20 h20', '', '2D2D2D')
    SetSolidButtonImage(linkButton, A_ScriptDir '\link-white.png', 12, -2, -2)
    linkButton.Visible := false
    settingsButton := SolidButton(window, 'x353 y12 w20 h20', '', '3C3C3C')
    SetSolidButtonImage(settingsButton, A_ScriptDir '\setting-white.png', 13, -1, -2)
    buttonStyles[settingsButton.Hwnd].iconOpacity := 0.72
    settingsButton.Visible := false
    rows := Map()
    for index, monitor in availableMonitors {
        y := 12 + (index - 1) * 96
        label := window.AddText('x27 y' y ' w155 h22 cFFFFFF BackgroundTrans +0x200', BrightnessPanelMonitorName(monitor))
        number := window.AddText('x218 y' y ' w70 h20 Center cFFFFFF BackgroundTrans', '')
        mainButton := 0
        if availableMonitors.Length > 1 {
            mainButton := SolidButton(window, 'x185 y' y ' w20 h20', '', '2D2D2D')
            SetSolidButtonImage(mainButton, A_ScriptDir '\main-display.ico', 13, -1, -2)
            mainButton.Visible := false
        }
        label.Visible := false
        number.Visible := false
        slider := BrightnessBar(window, 16, y + 50, 368, 24)
        try {
            brightnessValues[monitor.key] := BrightnessRead(monitor)
            slider.SetImmediate(brightnessValues[monitor.key])
        } catch {
            slider.Enabled := false
        }
        displayName := BrightnessDisplayName(monitor)
        rows[monitor.key] := {label: label, number: number, numberShown: true,
            slider: slider, name: BrightnessPanelMonitorName(monitor),
            displayName: displayName, mainAvailable: displayName != '',
            mainButton: mainButton}
    }
    height := 110 + (availableMonitors.Length - 1) * 96
    status := {Value: ''}
    brightnessPanel := {window: window, rows: rows, status: status,
        link: linkButton, settings: settingsButton, iconHover: '', mainHoverKey: '',
        x: 0, y: 0, bottom: 0, height: height, linkedView: false, updating: false,
        dragKey: '', numericActive: false, numericInput: '', numericKey: '',
        numericOverlayKey: '', numericOpacity: 0, numericSeconds: 2,
        numericLastDigit: 0,
        progress: 0, target: 1, from: 0, started: A_TickCount, duration: 280,
        fadeEntrance: !uiTest, highlightKey: '', highlightedKey: '', highlightLinked: false}
    for _, row in rows
        row.slider.Render()
    window.OnEvent('Close', HideBrightnessPanel)
    BrightnessRefreshPanel()
    height := brightnessPanel.height
    MonitorGetWorkArea(MonitorGetPrimary(), &left, &top, &right, &bottom)
    x := Max(left + 8, right - 408)
    y := Max(top, bottom - height)
    brightnessPanel.x := x
    brightnessPanel.y := y
    brightnessPanel.bottom := bottom
    window.Show('Hide x' x ' y' brightnessPanel.y ' w400 h' height)
    ApplyDarkWindow(window)
    BrightnessSetVisibleRegion(brightnessPanel, height)
    DllCall('user32\RedrawWindow', 'Ptr', window.Hwnd, 'Ptr', 0, 'Ptr', 0, 'UInt', 0x85)
    if uiTest {
        brightnessPanel.progress := 1
        window.Show('NoActivate x' x ' y' y)
    } else {
        style := DllCall('user32\GetWindowLongPtrW', 'Ptr', window.Hwnd, 'Int', -20, 'Ptr')
        DllCall('user32\SetWindowLongPtrW', 'Ptr', window.Hwnd, 'Int', -20,
            'Ptr', style | 0x80000, 'Ptr')
        DllCall('user32\SetLayeredWindowAttributes', 'Ptr', window.Hwnd,
            'UInt', 0, 'UChar', 0, 'UInt', 2)
        window.Show('NoActivate')
        DllCall('user32\RedrawWindow', 'Ptr', window.Hwnd, 'Ptr', 0, 'Ptr', 0,
            'UInt', 0x185)
        DllCall('dwmapi\DwmFlush')
        brightnessPanel.started := A_TickCount
        SetTimer(BrightnessAnimate, 15)
    }
    brightnessLastActivity := A_TickCount
    brightnessPointerDown := GetKeyState('LButton', 'P') || GetKeyState('RButton', 'P')
    SetTimer(BrightnessHideCheck, 30)
}

BrightnessSuppressErase(dc, lParam, msg, hwnd) {
    global brightnessPanel
    if IsObject(brightnessPanel) && hwnd = brightnessPanel.window.Hwnd
        return 1
}

BrightnessPaint(wParam, lParam, msg, hwnd) {
    global brightnessPanel, brightnessPainting
    panel := brightnessPanel
    if !IsObject(panel) || hwnd != panel.window.Hwnd
        return
    brightnessPainting += 1
    try {
        paint := Buffer(A_PtrSize = 8 ? 72 : 64, 0)
        dc := DllCall('user32\BeginPaint', 'Ptr', hwnd, 'Ptr', paint, 'Ptr')
        if dc {
            try BrightnessDrawPanel(dc, hwnd, panel)
            finally DllCall('user32\EndPaint', 'Ptr', hwnd, 'Ptr', paint)
        }
    } finally {
        brightnessPainting -= 1
    }
    return 0
}

BrightnessDrawPanel(dc, hwnd, panel) {
    global availableMonitors
    bounds := Buffer(16, 0)
    DllCall('user32\GetClientRect', 'Ptr', hwnd, 'Ptr', bounds)
    width := NumGet(bounds, 8, 'Int')
    height := NumGet(bounds, 12, 'Int')
    memoryDc := DllCall('gdi32\CreateCompatibleDC', 'Ptr', dc, 'Ptr')
    bitmap := memoryDc ? DllCall('gdi32\CreateCompatibleBitmap', 'Ptr', dc,
        'Int', width, 'Int', height, 'Ptr') : 0
    oldBitmap := bitmap ? DllCall('gdi32\SelectObject', 'Ptr', memoryDc,
        'Ptr', bitmap, 'Ptr') : 0
    canvas := oldBitmap ? memoryDc : dc
    brush := DllCall('gdi32\CreateSolidBrush', 'UInt', GdiColor('1E1E1E'), 'Ptr')
    DllCall('user32\FillRect', 'Ptr', canvas, 'Ptr', bounds, 'Ptr', brush)
    DllCall('gdi32\DeleteObject', 'Ptr', brush)
    if availableMonitors.Length > 1 && !panel.linkedView
        && panel.highlightKey != '' {
        shape := BrightnessHighlightBounds(panel, panel.highlightKey)
        top := panel.HasOwnProp('highlightTop')
            ? panel.highlightTop : shape.top
        bottom := panel.HasOwnProp('highlightBottom')
            ? panel.highlightBottom : shape.bottom
        BrightnessDrawShape(canvas, 8, Round(top), 392, Round(bottom), '292929')
    }
    BrightnessDrawPanelRows(canvas, hwnd, panel, width, height)
    if panel.status.Value != ''
        BrightnessDrawStatus(canvas, panel)
    if panel.numericOpacity > 0 && panel.numericOverlayKey != ''
        BrightnessDrawTypingOverlay(canvas, panel, width, height)
    BrightnessDrawPanelIcons(canvas, panel)
    if oldBitmap {
        opacity := panel.HasOwnProp('contentOpacity')
            ? panel.contentOpacity : 255
        if opacity < 255 {
            backgroundDc := DllCall('gdi32\CreateCompatibleDC', 'Ptr', dc, 'Ptr')
            backgroundBitmap := backgroundDc ? DllCall('gdi32\CreateCompatibleBitmap',
                'Ptr', dc, 'Int', width, 'Int', height, 'Ptr') : 0
            oldBackground := backgroundBitmap ? DllCall('gdi32\SelectObject',
                'Ptr', backgroundDc, 'Ptr', backgroundBitmap, 'Ptr') : 0
            if oldBackground {
                BrightnessFillRect(backgroundDc, 0, 0, width, height, '1E1E1E')
                if opacity > 0
                    DllCall('msimg32\AlphaBlend', 'Ptr', backgroundDc,
                        'Int', 0, 'Int', 0, 'Int', width, 'Int', height,
                        'Ptr', memoryDc, 'Int', 0, 'Int', 0,
                        'Int', width, 'Int', height, 'UInt', opacity << 16)
                DllCall('gdi32\BitBlt', 'Ptr', memoryDc,
                    'Int', 0, 'Int', 0, 'Int', width, 'Int', height,
                    'Ptr', backgroundDc, 'Int', 0, 'Int', 0, 'UInt', 0xCC0020)
                DllCall('gdi32\SelectObject', 'Ptr', backgroundDc,
                    'Ptr', oldBackground)
            }
            if backgroundBitmap
                DllCall('gdi32\DeleteObject', 'Ptr', backgroundBitmap)
            if backgroundDc
                DllCall('gdi32\DeleteDC', 'Ptr', backgroundDc)
        }
        DllCall('gdi32\BitBlt', 'Ptr', dc, 'Int', 0, 'Int', 0,
            'Int', width, 'Int', height, 'Ptr', memoryDc,
            'Int', 0, 'Int', 0, 'UInt', 0xCC0020)
        DllCall('gdi32\SelectObject', 'Ptr', memoryDc, 'Ptr', oldBitmap)
    }
    if bitmap
        DllCall('gdi32\DeleteObject', 'Ptr', bitmap)
    if memoryDc
        DllCall('gdi32\DeleteDC', 'Ptr', memoryDc)
    return 1
}

BrightnessDrawPanelRow(dc, hwnd, row) {
    BrightnessDrawControlText(dc, hwnd, row.label, row.name, false)
    if row.numberShown
        BrightnessDrawControlText(dc, hwnd, row.number, row.number.Value, true)
    if row.slider.Visible
        BrightnessDrawSlider(dc, row.slider)
}

BrightnessDrawPanelRows(dc, hwnd, panel, width, height) {
    global availableMonitors
    if panel.numericOpacity <= 0 || panel.numericOverlayKey = '' {
        for _, row in panel.rows
            BrightnessDrawPanelRow(dc, hwnd, row)
        return
    }
    area := BrightnessTypingBounds(panel)
    if panel.linkedView {
        BrightnessDrawDimmedRows(dc, hwnd, panel.rows, area, width, height,
            panel.numericOpacity)
        first := panel.rows[availableMonitors[1].key]
        if panel.numericActive
            BrightnessDrawControlText(dc, hwnd, first.number, 'Listening', true)
        return
    }
    for key, row in panel.rows {
        if key = panel.numericOverlayKey {
            BrightnessDrawDimmedRows(dc, hwnd, [row], area, width, height,
                panel.numericOpacity)
            if panel.numericActive
                BrightnessDrawControlText(dc, hwnd, row.number, 'Listening', true)
        } else
            BrightnessDrawPanelRow(dc, hwnd, row)
    }
}

BrightnessDrawDimmedRows(dc, hwnd, rows, area, width, height, fade) {
    scratch := DllCall('gdi32\CreateCompatibleDC', 'Ptr', dc, 'Ptr')
    bitmap := scratch ? DllCall('gdi32\CreateCompatibleBitmap', 'Ptr', dc,
        'Int', width, 'Int', height, 'Ptr') : 0
    oldBitmap := bitmap ? DllCall('gdi32\SelectObject', 'Ptr', scratch,
        'Ptr', bitmap, 'Ptr') : 0
    if oldBitmap {
        DllCall('gdi32\BitBlt', 'Ptr', scratch, 'Int', 0, 'Int', 0,
            'Int', width, 'Int', height, 'Ptr', dc,
            'Int', 0, 'Int', 0, 'UInt', 0xCC0020)
        for _, row in rows
            BrightnessDrawPanelRow(scratch, hwnd, row)
        visibility := Round(255 - 204 * fade)
        DllCall('msimg32\AlphaBlend', 'Ptr', dc,
            'Int', 8, 'Int', area.top, 'Int', 384,
            'Int', area.bottom - area.top, 'Ptr', scratch,
            'Int', 8, 'Int', area.top, 'Int', 384,
            'Int', area.bottom - area.top, 'UInt', visibility << 16)
        DllCall('gdi32\SelectObject', 'Ptr', scratch, 'Ptr', oldBitmap)
    } else
        for _, row in rows
            BrightnessDrawPanelRow(dc, hwnd, row)
    if bitmap
        DllCall('gdi32\DeleteObject', 'Ptr', bitmap)
    if scratch
        DllCall('gdi32\DeleteDC', 'Ptr', scratch)
}

BrightnessDrawTypingOverlay(dc, panel, width, height) {
    scratch := DllCall('gdi32\CreateCompatibleDC', 'Ptr', dc, 'Ptr')
    bitmap := scratch ? DllCall('gdi32\CreateCompatibleBitmap', 'Ptr', dc,
        'Int', width, 'Int', height, 'Ptr') : 0
    oldBitmap := bitmap ? DllCall('gdi32\SelectObject', 'Ptr', scratch,
        'Ptr', bitmap, 'Ptr') : 0
    if oldBitmap {
        DllCall('gdi32\BitBlt', 'Ptr', scratch, 'Int', 0, 'Int', 0,
            'Int', width, 'Int', height, 'Ptr', dc,
            'Int', 0, 'Int', 0, 'UInt', 0xCC0020)
        area := BrightnessTypingBounds(panel)
        BrightnessDrawTypingText(scratch, panel, area)
        DllCall('msimg32\AlphaBlend', 'Ptr', dc,
            'Int', 8, 'Int', area.top, 'Int', 384,
            'Int', area.bottom - area.top, 'Ptr', scratch,
            'Int', 8, 'Int', area.top, 'Int', 384,
            'Int', area.bottom - area.top,
            'UInt', Round(255 * panel.numericOpacity) << 16)
        DllCall('gdi32\SelectObject', 'Ptr', scratch, 'Ptr', oldBitmap)
    }
    if bitmap
        DllCall('gdi32\DeleteObject', 'Ptr', bitmap)
    if scratch
        DllCall('gdi32\DeleteDC', 'Ptr', scratch)
}

BrightnessDrawTypingText(dc, panel, area) {
    global availableMonitors
    height := area.bottom - area.top
    digitBottom := area.top + Round(height * 0.77)
    digitRect := Buffer(16, 0)
    NumPut('Int', 8, digitRect, 0)
    NumPut('Int', area.top + Min(25, Round(height * 0.28)), digitRect, 4)
    NumPut('Int', 392, digitRect, 8)
    NumPut('Int', digitBottom, digitRect, 12)
    largeFont := DllCall('gdi32\CreateFontW',
        'Int', -Round(height * 0.405), 'Int', 0, 'Int', 0, 'Int', 0,
        'Int', 700, 'UInt', 0, 'UInt', 0, 'UInt', 0,
        'UInt', 1, 'UInt', 0, 'UInt', 0, 'UInt', 5,
        'UInt', 0, 'WStr', 'Segoe UI', 'Ptr')
    oldFont := largeFont ? DllCall('gdi32\SelectObject', 'Ptr', dc,
        'Ptr', largeFont, 'Ptr') : 0
    DllCall('gdi32\SetBkMode', 'Ptr', dc, 'Int', 1)
    BrightnessDrawGlowText(dc, panel.numericInput, digitRect, area)
    if oldFont
        DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', oldFont)
    if largeFont
        DllCall('gdi32\DeleteObject', 'Ptr', largeFont)
    countdownRect := Buffer(16, 0)
    NumPut('Int', 8, countdownRect, 0)
    NumPut('Int', digitBottom - 2, countdownRect, 4)
    NumPut('Int', 392, countdownRect, 8)
    NumPut('Int', area.bottom - 3, countdownRect, 12)
    label := panel.rows[availableMonitors[1].key].label
    font := DllCall('user32\SendMessageW', 'Ptr', label.Hwnd,
        'UInt', 0x31, 'Ptr', 0, 'Ptr', 0, 'Ptr')
    oldFont := font ? DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', font,
        'Ptr') : 0
    DllCall('gdi32\SetTextColor', 'Ptr', dc, 'UInt', GdiColor('D4D4D4'))
    DllCall('user32\DrawTextW', 'Ptr', dc, 'Str',
        'Catching: ' panel.numericSeconds ' sec',
        'Int', -1, 'Ptr', countdownRect, 'UInt', 0x825)
    if oldFont
        DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', oldFont)
}

BrightnessDrawGlowText(dc, value, rect, area) {
    static glow := {ambientKey: '', shadowKey: '', ambient: 0, shadow: 0}
    left := 8, top := area.top
    width := 384, height := area.bottom - area.top
    textTop := NumGet(rect, 4, 'Int') - top
    textBottom := NumGet(rect, 12, 'Int') - top
    digitSize := Round(height * 0.405)
    centerY := (textTop + textBottom) / 2
    ambientKey := height '|' centerY '|' digitSize
    if glow.ambientKey != ambientKey {
        BrightnessReleaseGlowLayer(glow.ambient)
        info := BrightnessDibInfo(width, height)
        glow.ambient := BrightnessRadialGlowLayer(dc, info, width, height,
            width / 2, centerY, Round(digitSize * 1.5))
        glow.ambientKey := ambientKey
    }
    shadowWidth := Min(width, Max(120, Round(digitSize * 3.5)))
    shadowHeight := textBottom - textTop + 8
    shadowKey := value '|' shadowWidth '|' shadowHeight
    if glow.shadowKey != shadowKey {
        BrightnessReleaseGlowLayer(glow.shadow)
        glow.shadow := BrightnessMakeShadow(dc, value, shadowWidth,
            shadowHeight)
        glow.shadowKey := shadowKey
    }
    BrightnessBlendGlowLayer(dc, glow.ambient, left, top, width, height)
    BrightnessBlendGlowLayer(dc, glow.shadow,
        left + Round((width - shadowWidth) / 2), top + textTop - 4,
        shadowWidth, shadowHeight)
    DllCall('gdi32\SetTextColor', 'Ptr', dc, 'UInt', GdiColor('FFFFFF'))
    DllCall('user32\DrawTextW', 'Ptr', dc, 'Str', value,
        'Int', -1, 'Ptr', rect, 'UInt', 0x825)
}

BrightnessBlendGlowLayer(dc, layer, left, top, width, height) {
    if !IsObject(layer) || !layer.dc
        return
    DllCall('msimg32\AlphaBlend', 'Ptr', dc, 'Int', left, 'Int', top,
        'Int', width, 'Int', height, 'Ptr', layer.dc,
        'Int', 0, 'Int', 0, 'Int', width, 'Int', height,
        'UInt', 0x01FF0000)
}

BrightnessReleaseGlowLayer(layer) {
    if !IsObject(layer) || !layer.dc
        return
    DllCall('gdi32\SelectObject', 'Ptr', layer.dc, 'Ptr', layer.previous, 'Ptr')
    DllCall('gdi32\DeleteObject', 'Ptr', layer.bitmap)
    DllCall('gdi32\DeleteDC', 'Ptr', layer.dc)
}

BrightnessDibInfo(width, height) {
    info := Buffer(40, 0)
    NumPut('UInt', 40, info, 0)
    NumPut('Int', width, info, 4)
    NumPut('Int', -height, info, 8)
    NumPut('UShort', 1, info, 12)
    NumPut('UShort', 32, info, 14)
    return info
}

BrightnessMakeShadow(dc, value, width, height) {
    if width <= 0 || height <= 0
        return 0
    info := BrightnessDibInfo(width, height)
    maskBits := 0
    maskBitmap := DllCall('gdi32\CreateDIBSection', 'Ptr', dc,
        'Ptr', info, 'UInt', 0, 'Ptr*', &maskBits, 'Ptr', 0, 'UInt', 0, 'Ptr')
    maskDc := maskBitmap ? DllCall('gdi32\CreateCompatibleDC', 'Ptr', dc, 'Ptr') : 0
    oldMask := maskDc ? DllCall('gdi32\SelectObject', 'Ptr', maskDc,
        'Ptr', maskBitmap, 'Ptr') : 0
    if !oldMask {
        if maskDc
            DllCall('gdi32\DeleteDC', 'Ptr', maskDc)
        if maskBitmap
            DllCall('gdi32\DeleteObject', 'Ptr', maskBitmap)
        return 0
    }
    DllCall('msvcrt\memset', 'Ptr', maskBits, 'Int', 0,
        'UPtr', width * height * 4, 'Ptr')
    font := DllCall('gdi32\GetCurrentObject', 'Ptr', dc, 'UInt', 6, 'Ptr')
    oldFont := font ? DllCall('gdi32\SelectObject', 'Ptr', maskDc, 'Ptr', font, 'Ptr') : 0
    textRect := Buffer(16, 0)
    NumPut('Int', 4, textRect, 4)
    NumPut('Int', width, textRect, 8)
    NumPut('Int', height - 4, textRect, 12)
    DllCall('gdi32\SetBkMode', 'Ptr', maskDc, 'Int', 1)
    DllCall('gdi32\SetTextColor', 'Ptr', maskDc, 'UInt', 0xFFFFFF)
    DllCall('user32\DrawTextW', 'Ptr', maskDc, 'Str', value,
        'Int', -1, 'Ptr', textRect, 'UInt', 0x825)
    if oldFont
        DllCall('gdi32\SelectObject', 'Ptr', maskDc, 'Ptr', oldFont)
    DllCall('gdi32\GdiFlush')
    shadow := BrightnessBlurredGlowLayer(dc, info, maskBits, width, height,
        3, 2.7, 0x0B1015)
    DllCall('gdi32\SelectObject', 'Ptr', maskDc, 'Ptr', oldMask, 'Ptr')
    DllCall('gdi32\DeleteObject', 'Ptr', maskBitmap)
    DllCall('gdi32\DeleteDC', 'Ptr', maskDc)
    return shadow
}

BrightnessRadialGlowLayer(dc, info, width, height, centerX, centerY, radius) {
    bits := 0
    bitmap := DllCall('gdi32\CreateDIBSection', 'Ptr', dc,
        'Ptr', info, 'UInt', 0, 'Ptr*', &bits, 'Ptr', 0, 'UInt', 0, 'Ptr')
    layerDc := bitmap ? DllCall('gdi32\CreateCompatibleDC', 'Ptr', dc, 'Ptr') : 0
    previous := layerDc ? DllCall('gdi32\SelectObject', 'Ptr', layerDc,
        'Ptr', bitmap, 'Ptr') : 0
    if !previous {
        if layerDc
            DllCall('gdi32\DeleteDC', 'Ptr', layerDc)
        if bitmap
            DllCall('gdi32\DeleteObject', 'Ptr', bitmap)
        return 0
    }
    DllCall('msvcrt\memset', 'Ptr', bits, 'Int', 0,
        'UPtr', width * height * 4, 'Ptr')
    firstX := Max(0, Ceil(centerX - radius))
    lastX := Min(width - 1, Floor(centerX + radius))
    radiusSquared := radius * radius
    Loop height {
        y := A_Index - 1
        dy := y - centerY
        if Abs(dy) >= radius
            continue
        Loop lastX - firstX + 1 {
            x := firstX + A_Index - 1
            dx := x - centerX
            distanceSquared := dx * dx + dy * dy
            if distanceSquared >= radiusSquared
                continue
            fade := 1 - Sqrt(distanceSquared) / radius
            alpha := Round(26 * fade * fade * (3 - 2 * fade))
            NumPut('UInt', (alpha << 24) | (alpha << 16)
                | (alpha << 8) | alpha, bits + 4 * (y * width + x))
        }
    }
    return {dc: layerDc, bitmap: bitmap, previous: previous}
}

BrightnessBlurredGlowLayer(dc, info, maskBits, width, height, radius,
    intensity, rgb) {
    horizontal := Buffer(width * height, 0)
    Loop height {
        y := A_Index - 1
        total := 0
        Loop Min(width, radius + 1)
            total += NumGet(maskBits + 4 * (y * width + A_Index - 1), 'UChar')
        Loop width {
            x := A_Index - 1
            count := Min(width - 1, x + radius) - Max(0, x - radius) + 1
            NumPut('UChar', Round(total / count), horizontal, y * width + x)
            if x - radius >= 0
                total -= NumGet(maskBits + 4 * (y * width + x - radius), 'UChar')
            if x + radius + 1 < width
                total += NumGet(maskBits + 4 * (y * width + x + radius + 1), 'UChar')
        }
    }
    bits := 0
    bitmap := DllCall('gdi32\CreateDIBSection', 'Ptr', dc,
        'Ptr', info, 'UInt', 0, 'Ptr*', &bits, 'Ptr', 0, 'UInt', 0, 'Ptr')
    layerDc := bitmap ? DllCall('gdi32\CreateCompatibleDC', 'Ptr', dc, 'Ptr') : 0
    previous := layerDc ? DllCall('gdi32\SelectObject', 'Ptr', layerDc,
        'Ptr', bitmap, 'Ptr') : 0
    if previous {
        red := (rgb >> 16) & 255
        green := (rgb >> 8) & 255
        blue := rgb & 255
        Loop width {
            x := A_Index - 1
            total := 0
            Loop Min(height, radius + 1)
                total += NumGet(horizontal, (A_Index - 1) * width + x, 'UChar')
            Loop height {
                y := A_Index - 1
                count := Min(height - 1, y + radius) - Max(0, y - radius) + 1
                alpha := Min(190, Round(total / count * intensity))
                color := (alpha << 24) | (Round(alpha * red / 255) << 16)
                    | (Round(alpha * green / 255) << 8)
                    | Round(alpha * blue / 255)
                NumPut('UInt', color, bits + 4 * (y * width + x))
                if y - radius >= 0
                    total -= NumGet(horizontal, (y - radius) * width + x, 'UChar')
                if y + radius + 1 < height
                    total += NumGet(horizontal, (y + radius + 1) * width + x, 'UChar')
            }
        }
    }
    if !previous {
        if layerDc
            DllCall('gdi32\DeleteDC', 'Ptr', layerDc)
        if bitmap
            DllCall('gdi32\DeleteObject', 'Ptr', bitmap)
        return 0
    }
    return {dc: layerDc, bitmap: bitmap, previous: previous}
}

BrightnessDrawPanelIcons(dc, panel) {
    global availableMonitors, buttonStyles
    DllCall('gdi32\GdiFlush')
    graphics := 0
    if DllCall('gdiplus\GdipCreateFromHDC', 'Ptr', dc, 'Ptr*', &graphics) != 0
        return
    DllCall('gdiplus\GdipSetInterpolationMode', 'Ptr', graphics, 'Int', 7)
    if availableMonitors.Length > 1 {
        primary := StrUpper(BrightnessMainDisplayName())
        for monitor in availableMonitors {
            row := panel.rows[monitor.key]
            if !row.mainAvailable
                continue
            style := buttonStyles[row.mainButton.Hwnd]
            active := StrUpper(row.displayName) = primary
            style.iconOpacity := active ? 1.0 : 0.42
            style.iconHoverOpacity := active ? 1.0 : 0.65
            BrightnessDrawPanelIcon(graphics, panel, row.mainButton, 'main',
                panel.mainHoverKey = monitor.key)
        }
    }
    if availableMonitors.Length > 1
        BrightnessDrawPanelIcon(graphics, panel, panel.link, 'link')
    BrightnessDrawPanelIcon(graphics, panel, panel.settings, 'settings')
    DllCall('gdiplus\GdipDeleteGraphics', 'Ptr', graphics)
}

BrightnessDrawPanelIcon(graphics, panel, control, kind, hovered := false) {
    global buttonStyles
    if !buttonStyles.Has(control.Hwnd)
        return
    style := buttonStyles[control.Hwnd]
    if !style.icon
        return
    rect := BrightnessControlRect(panel.window.Hwnd, control.Hwnd)
    size := style.iconSize
    x := NumGet(rect, 0, 'Int') + Round((20 - size) / 2) + style.iconOffsetX
    y := NumGet(rect, 4, 'Int') + Round((20 - size) / 2)
        + style.iconOffsetY + 2
    opacity := hovered || panel.iconHover = kind
        ? style.iconHoverOpacity : style.iconOpacity
    attributes := 0
    if opacity < 1 {
        matrix := Buffer(100, 0)
        for offset in [0, 24, 48, 96]
            NumPut('Float', 1.0, matrix, offset)
        NumPut('Float', opacity, matrix, 72)
        if DllCall('gdiplus\GdipCreateImageAttributes', 'Ptr*', &attributes) = 0
            DllCall('gdiplus\GdipSetImageAttributesColorMatrix', 'Ptr', attributes,
                'Int', 0, 'Int', 1, 'Ptr', matrix, 'Ptr', 0, 'Int', 0)
    }
    sourceWidth := 0, sourceHeight := 0
    DllCall('gdiplus\GdipGetImageWidth', 'Ptr', style.icon, 'UInt*', &sourceWidth)
    DllCall('gdiplus\GdipGetImageHeight', 'Ptr', style.icon, 'UInt*', &sourceHeight)
    DllCall('gdiplus\GdipDrawImageRectRectI', 'Ptr', graphics, 'Ptr', style.icon,
        'Int', x, 'Int', y, 'Int', size, 'Int', size,
        'Int', 0, 'Int', 0, 'Int', sourceWidth, 'Int', sourceHeight,
        'Int', 2, 'Ptr', attributes, 'Ptr', 0, 'Ptr', 0)
    if attributes
        DllCall('gdiplus\GdipDisposeImageAttributes', 'Ptr', attributes)
}

BrightnessFillRect(dc, x, y, width, height, color) {
    rect := Buffer(16, 0)
    NumPut('Int', x, rect, 0)
    NumPut('Int', y, rect, 4)
    NumPut('Int', x + width, rect, 8)
    NumPut('Int', y + height, rect, 12)
    brush := DllCall('gdi32\CreateSolidBrush', 'UInt', GdiColor(color), 'Ptr')
    DllCall('user32\FillRect', 'Ptr', dc, 'Ptr', rect, 'Ptr', brush)
    DllCall('gdi32\DeleteObject', 'Ptr', brush)
}

BrightnessDrawSlider(dc, slider) {
    center := Round(11 + (slider.width - 22) * slider._display / 100)
    BrightnessFillRect(dc, slider.x + 11, slider.y + 9,
        slider.width - 22, 6, '404040')
    BrightnessFillRect(dc, slider.x + 11, slider.y + 9,
        Max(1, center - 11), 6, 'FFFFFF')
    BrightnessFillRect(dc, slider.x + center - 5, slider.y + 2,
        10, 20, 'E6E6E6')
}

BrightnessControlRect(windowHwnd, controlHwnd) {
    rect := Buffer(16, 0)
    DllCall('user32\GetWindowRect', 'Ptr', controlHwnd, 'Ptr', rect)
    DllCall('user32\ScreenToClient', 'Ptr', windowHwnd, 'Ptr', rect.Ptr)
    DllCall('user32\ScreenToClient', 'Ptr', windowHwnd, 'Ptr', rect.Ptr + 8)
    return rect
}

BrightnessDrawControlText(dc, windowHwnd, control, value, centered, color := 'FFFFFF') {
    if value = ''
        return
    rect := BrightnessControlRect(windowHwnd, control.Hwnd)
    font := DllCall('user32\SendMessageW', 'Ptr', control.Hwnd,
        'UInt', 0x31, 'Ptr', 0, 'Ptr', 0, 'Ptr')
    oldFont := font ? DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', font, 'Ptr') : 0
    DllCall('gdi32\SetBkMode', 'Ptr', dc, 'Int', 1)
    DllCall('gdi32\SetTextColor', 'Ptr', dc, 'UInt', GdiColor(color))
    DllCall('user32\DrawTextW', 'Ptr', dc, 'Str', value, 'Int', -1,
        'Ptr', rect, 'UInt', 0x8824 | (centered ? 1 : 0))
    if oldFont
        DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', oldFont)
}

BrightnessDrawStatus(dc, panel) {
    global availableMonitors
    rect := Buffer(16, 0)
    NumPut('Int', 16, rect, 0)
    NumPut('Int', panel.height - 22, rect, 4)
    NumPut('Int', 380, rect, 8)
    NumPut('Int', panel.height - 4, rect, 12)
    label := panel.rows[availableMonitors[1].key].label
    font := DllCall('user32\SendMessageW', 'Ptr', label.Hwnd,
        'UInt', 0x31, 'Ptr', 0, 'Ptr', 0, 'Ptr')
    oldFont := font ? DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', font, 'Ptr') : 0
    DllCall('gdi32\SetBkMode', 'Ptr', dc, 'Int', 1)
    DllCall('gdi32\SetTextColor', 'Ptr', dc, 'UInt', GdiColor('B7B7B7'))
    DllCall('user32\DrawTextW', 'Ptr', dc, 'Str', panel.status.Value,
        'Int', -1, 'Ptr', rect, 'UInt', 0x8824)
    if oldFont
        DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', oldFont)
}

BrightnessInvalidateStatus(panel) {
    if !panel.bottom
        return
    rect := Buffer(16, 0)
    NumPut('Int', 16, rect, 0)
    NumPut('Int', panel.height - 22, rect, 4)
    NumPut('Int', 380, rect, 8)
    NumPut('Int', panel.height - 4, rect, 12)
    DllCall('user32\RedrawWindow', 'Ptr', panel.window.Hwnd,
        'Ptr', rect, 'Ptr', 0, 'UInt', 0x105)
}

BrightnessInvalidateText(panel, control) {
    rect := BrightnessControlRect(panel.window.Hwnd, control.Hwnd)
    DllCall('user32\RedrawWindow', 'Ptr', panel.window.Hwnd,
        'Ptr', rect, 'Ptr', 0, 'UInt', 0x105)
}

BrightnessUpdateHighlight(panel) {
    global availableMonitors
    if availableMonitors.Length < 2
        return
    if !panel.bottom
        return
    bounds := Buffer(16, 0)
    NumPut('Int', 8, bounds, 0)
    NumPut('Int', 7, bounds, 4)
    NumPut('Int', 392, bounds, 8)
    ; Preserve full-height invalidation: the last highlight reaches height - 7.
    ; Stopping at height - 27 leaves a stale dark strip when selection changes.
    NumPut('Int', panel.height, bounds, 12)
    DllCall('user32\RedrawWindow', 'Ptr', panel.window.Hwnd, 'Ptr', bounds,
        'Ptr', 0, 'UInt', 0x105)
    panel.highlightedKey := panel.highlightKey
    panel.highlightLinked := panel.linkedView
}

BrightnessHighlightBounds(panel, key) {
    global availableMonitors
    for index, monitor in availableMonitors
        if monitor.key = key {
            top := 7 + (index - 1) * 96
            return {top: top, bottom: index = availableMonitors.Length
                ? panel.height - 7 : top + 90}
        }
    return {top: 7, bottom: 97}
}

BrightnessRetargetHighlight(panel) {
    global uiTest
    if panel.highlightKey = ''
        return
    bounds := BrightnessHighlightBounds(panel, panel.highlightKey)
    if !panel.HasOwnProp('highlightTop') || !panel.bottom || panel.linkedView
        || uiTest {
        panel.highlightTop := bounds.top
        panel.highlightBottom := bounds.bottom
        BrightnessUpdateHighlight(panel)
        return
    }
    panel.highlightFromTop := panel.highlightTop
    panel.highlightFromBottom := panel.highlightBottom
    panel.highlightToTop := bounds.top
    panel.highlightToBottom := bounds.bottom
    panel.highlightStarted := A_TickCount
    SetTimer(BrightnessAnimateHighlight, 15)
}

BrightnessAnimateHighlight(*) {
    global brightnessPanel
    if !IsObject(brightnessPanel) || !brightnessPanel.HasOwnProp('highlightStarted') {
        SetTimer(BrightnessAnimateHighlight, 0)
        return
    }
    panel := brightnessPanel
    elapsed := Min(1, Max(0, (A_TickCount - panel.highlightStarted) / 220))
    eased := elapsed * elapsed * (3 - 2 * elapsed)
    panel.highlightTop := panel.highlightFromTop
        + (panel.highlightToTop - panel.highlightFromTop) * eased
    panel.highlightBottom := panel.highlightFromBottom
        + (panel.highlightToBottom - panel.highlightFromBottom) * eased
    BrightnessUpdateHighlight(panel)
    if elapsed >= 1
        SetTimer(BrightnessAnimateHighlight, 0)
}

BrightnessRefreshPanel() {
    global brightnessPanel, brightnessValues, brightnessSelectedKey, brightnessLinked, buttonStyles, availableMonitors
    if !IsObject(brightnessPanel)
        return
    desiredLinked := brightnessLinked && availableMonitors.Length > 1
    if brightnessPanel.HasOwnProp('layouting') && brightnessPanel.layouting {
        if brightnessPanel.layoutTarget != desiredLinked
            BrightnessLayoutPanel(brightnessPanel)
    } else if brightnessPanel.linkedView != desiredLinked
        BrightnessLayoutPanel(brightnessPanel)
    brightnessPanel.updating := true
    newHighlight := availableMonitors.Length > 1 ? brightnessSelectedKey : ''
    if brightnessPanel.highlightKey != newHighlight {
        brightnessPanel.highlightKey := newHighlight
        BrightnessRetargetHighlight(brightnessPanel)
    }
    for key, row in brightnessPanel.rows {
        if brightnessPanel.linkedView && key != availableMonitors[1].key
            continue
        typingHere := brightnessPanel.numericActive
            && key = (brightnessPanel.linkedView
                ? availableMonitors[1].key : brightnessPanel.numericKey)
        if brightnessValues.Has(key) {
            label := brightnessValues[key] '%'
            if !typingHere && row.number.Value != label {
                row.number.Value := label
                if brightnessPanel.bottom && row.numberShown
                    BrightnessInvalidateText(brightnessPanel, row.number)
            }
            if row.slider.Value != brightnessValues[key]
                row.slider.Value := brightnessValues[key]
        } else if !typingHere && row.number.Value != 'N/A' {
            row.number.Value := 'N/A'
            if brightnessPanel.bottom && row.numberShown
                BrightnessInvalidateText(brightnessPanel, row.number)
        }
    }
    if buttonStyles.Has(brightnessPanel.link.Hwnd) {
        style := buttonStyles[brightnessPanel.link.Hwnd]
        opacity := brightnessLinked ? 1.0 : 0.42
        changed := style.iconOpacity != opacity
        style.iconOpacity := opacity
        style.iconHoverOpacity := brightnessLinked ? 1.0 : 0.65
        if changed && brightnessPanel.bottom
            BrightnessInvalidateIconArea(brightnessPanel)
    }
    brightnessPanel.updating := false
}

BrightnessLayoutPanel(panel) {
    global brightnessLinked, availableMonitors, uiTest
    if panel.bottom && !uiTest {
        BrightnessStartLayout(panel, brightnessLinked && availableMonitors.Length > 1)
        return
    }
    BrightnessApplyLayout(panel, brightnessLinked && availableMonitors.Length > 1)
}

BrightnessApplyLayout(panel, linked) {
    global availableMonitors
    panel.linkedView := linked
    first := panel.rows[availableMonitors[1].key]
    if panel.linkedView {
        first.slider.Move(16, 24 + 28 * availableMonitors.Length)
    } else {
        first.slider.Move(16, 62)
    }
    for index, monitor in availableMonitors {
        row := panel.rows[monitor.key]
        row.number.Move(218)
        if panel.linkedView {
            y := 12 + (index - 1) * 28
            row.label.Move(27, y, 155, 20)
            if IsObject(row.mainButton)
                row.mainButton.Move(185, y)
            row.numberShown := index = 1
            row.slider.Visible := index = 1
        } else {
            y := 12 + (index - 1) * 96
            row.label.Move(27, y, 155, 20)
            if IsObject(row.mainButton)
                row.mainButton.Move(185, y)
            row.numberShown := true
            row.slider.Visible := true
        }
    }
    panel.height := panel.linkedView ? 72 + 28 * availableMonitors.Length
        : 110 + (availableMonitors.Length - 1) * 96
    if panel.highlightKey != '' {
        shape := BrightnessHighlightBounds(panel, panel.highlightKey)
        panel.highlightTop := shape.top
        panel.highlightBottom := shape.bottom
        SetTimer(BrightnessAnimateHighlight, 0)
    }
    BrightnessUpdateHighlight(panel)
    if panel.bottom {
        panel.y := panel.bottom - panel.height
        panel.window.Show('NoActivate x' panel.x ' y' panel.y ' w400 h' panel.height)
        BrightnessRenderFrame(panel, panel.progress)
    }
}

BrightnessStartLayout(panel, linked) {
    global brightnessLastActivity
    panel.layoutTarget := linked
    panel.layoutStarted := A_TickCount
    panel.layoutApplied := false
    panel.layouting := true
    panel.layoutFromOpacity := panel.HasOwnProp('contentOpacity')
        ? panel.contentOpacity : 255
    brightnessLastActivity := A_TickCount
    SetTimer(BrightnessAnimateLayout, 15)
    BrightnessAnimateLayout()
}

BrightnessAnimateLayout(*) {
    global brightnessPanel, brightnessLastActivity
    if !IsObject(brightnessPanel) || !brightnessPanel.HasOwnProp('layouting')
        || !brightnessPanel.layouting {
        SetTimer(BrightnessAnimateLayout, 0)
        return
    }
    panel := brightnessPanel
    elapsed := Min(1, Max(0, (A_TickCount - panel.layoutStarted) / 240))
    half := elapsed < 0.5 ? elapsed * 2 : (elapsed - 0.5) * 2
    eased := half * half * (3 - 2 * half)
    if elapsed >= 0.5 && !panel.layoutApplied {
        panel.contentOpacity := 0
        BrightnessApplyLayout(panel, panel.layoutTarget)
        panel.layoutApplied := true
        BrightnessRefreshPanel()
    }
    panel.contentOpacity := Round(elapsed < 0.5
        ? panel.layoutFromOpacity * (1 - eased) : 255 * eased)
    DllCall('user32\RedrawWindow', 'Ptr', panel.window.Hwnd, 'Ptr', 0,
        'Ptr', 0, 'UInt', 0x105)
    if elapsed < 1
        return
    SetTimer(BrightnessAnimateLayout, 0)
    panel.contentOpacity := 255
    panel.layouting := false
    brightnessLastActivity := A_TickCount
}

BrightnessSliderChanged(key, control, *) {
    global brightnessPanel
    if !IsObject(brightnessPanel) || brightnessPanel.updating
        return
    BrightnessQueueValue(key, control.Value)
}

BrightnessQueueValue(key, value, motion := 'auto') {
    global brightnessPanel, brightnessSelectedKey, brightnessLastActivity, brightnessPending, brightnessValues, brightnessLinked, availableMonitors
    BrightnessCancelTypedValue()
    value := BrightnessClamp(value)
    brightnessSelectedKey := key
    brightnessValues[key] := value
    if brightnessLinked
        for monitor in availableMonitors
            brightnessValues[monitor.key] := value
    if motion != 'auto' && IsObject(brightnessPanel)
        for monitor in availableMonitors
            if (monitor.key = key || brightnessLinked)
                && brightnessPanel.rows.Has(monitor.key) {
                slider := brightnessPanel.rows[monitor.key].slider
                if motion = 'animated'
                    slider.SetAnimated(value)
                else
                    slider.SetImmediate(value)
            }
    BrightnessRefreshPanel()
    brightnessPending[key] := value
    brightnessLastActivity := A_TickCount
    SetTimer(BrightnessFlushSlider, -80)
}

BrightnessSliderMouseDown(wParam, lParam, msg, hwnd) {
    global brightnessPanel, availableMonitors
    if !IsObject(brightnessPanel)
        return
    panel := brightnessPanel
    if hwnd != panel.window.Hwnd {
        found := false
        for _, row in panel.rows
            if row.slider.ContainsHwnd(hwnd) {
                found := true
                break
            }
        if !found
            return
    }
    point := BrightnessPointerClient(panel.window.Hwnd)
    if hwnd = panel.window.Hwnd {
        mainKey := BrightnessMainIconAtPoint(panel, point)
        if mainKey != '' {
            BrightnessSetMainDisplay(mainKey)
            return 0
        }
        icon := BrightnessIconAtPoint(panel, point)
        if icon = 'link' {
            ToggleBrightnessLink()
            return 0
        }
        if icon = 'settings' {
            OpenBrightnessSettings()
            return 0
        }
    }
    for key, row in brightnessPanel.rows {
        slider := row.slider
        if !slider.Visible || point.x < slider.x || point.x >= slider.x + slider.width
            || point.y < slider.y || point.y >= slider.y + slider.height
            continue
        if !slider.Enabled
            return 0
        panel.dragKey := key
        DllCall('user32\SetCapture', 'Ptr', panel.window.Hwnd)
        BrightnessBarAtMouse(key, slider, lParam)
        return 0
    }
    if hwnd = panel.window.Hwnd && !panel.linkedView
        && !panel.numericActive && panel.numericOpacity <= 0
        for index, monitor in availableMonitors
            if point.y >= 7 + (index - 1) * 96 && point.y < 97 + (index - 1) * 96 {
                SelectBrightnessMonitor(monitor.key)
                return 0
            }
}

BrightnessBarMouseMove(wParam, lParam, msg, hwnd) {
    global brightnessPanel, availableMonitors, brightnessSelectedKey
    if !IsObject(brightnessPanel)
        return
    if brightnessPanel.dragKey = '' {
        point := BrightnessPointerClient(brightnessPanel.window.Hwnd)
        mainKey := hwnd = brightnessPanel.window.Hwnd
            ? BrightnessMainIconAtPoint(brightnessPanel, point) : ''
        if brightnessPanel.mainHoverKey != mainKey {
            brightnessPanel.mainHoverKey := mainKey
            BrightnessInvalidateMainIcons(brightnessPanel)
        }
        icon := hwnd = brightnessPanel.window.Hwnd
            ? BrightnessIconAtPoint(brightnessPanel, point) : ''
        if brightnessPanel.iconHover != icon {
            brightnessPanel.iconHover := icon
            BrightnessInvalidateIconArea(brightnessPanel)
        }
        if icon = '' && !brightnessPanel.numericActive
            && brightnessPanel.numericOpacity <= 0
            && !brightnessPanel.linkedView
            && availableMonitors.Length > 1 && point.x >= 8 && point.x < 392 {
            for index, monitor in availableMonitors {
                top := 7 + (index - 1) * 96
                bottom := index = availableMonitors.Length
                    ? brightnessPanel.height - 7 : top + 90
                if point.y >= top && point.y < bottom {
                    if brightnessSelectedKey != monitor.key
                        SelectBrightnessMonitor(monitor.key)
                    break
                }
            }
        }
        return
    }
    if !GetKeyState('LButton', 'P')
        return
    row := brightnessPanel.rows[brightnessPanel.dragKey]
    BrightnessBarAtMouse(brightnessPanel.dragKey, row.slider, lParam)
    return 0
}

BrightnessIconAtPoint(panel, point) {
    global availableMonitors
    if point.y < 10 || point.y >= 34
        return ''
    if availableMonitors.Length > 1 && point.x >= 323 && point.x < 347
        return 'link'
    if point.x >= 351 && point.x < 375
        return 'settings'
    return ''
}

BrightnessMainIconAtPoint(panel, point) {
    global availableMonitors
    if availableMonitors.Length < 2 || point.x < 183 || point.x >= 205
        return ''
    for monitor in availableMonitors {
        row := panel.rows[monitor.key]
        if !row.mainAvailable
            continue
        rect := BrightnessControlRect(panel.window.Hwnd, row.label.Hwnd)
        y := NumGet(rect, 4, 'Int')
        if point.y >= y && point.y < y + 21
            return monitor.key
    }
    return ''
}

BrightnessInvalidateMainIcons(panel) {
    if !IsObject(panel) || !panel.bottom
        return
    rect := Buffer(16, 0)
    NumPut('Int', 181, rect, 0), NumPut('Int', 8, rect, 4)
    NumPut('Int', 207, rect, 8), NumPut('Int', panel.height - 8, rect, 12)
    DllCall('user32\RedrawWindow', 'Ptr', panel.window.Hwnd, 'Ptr', rect,
        'Ptr', 0, 'UInt', 0x105)
}

BrightnessInvalidateIconArea(panel) {
    if !panel.bottom
        return
    rect := Buffer(16, 0)
    NumPut('Int', 319, rect, 0), NumPut('Int', 8, rect, 4)
    NumPut('Int', 379, rect, 8), NumPut('Int', 36, rect, 12)
    DllCall('user32\RedrawWindow', 'Ptr', panel.window.Hwnd, 'Ptr', rect,
        'Ptr', 0, 'UInt', 0x105)
}

BrightnessBarMouseUp(wParam, lParam, msg, hwnd) {
    global brightnessPanel
    if !IsObject(brightnessPanel) || brightnessPanel.dragKey = ''
        return
    key := brightnessPanel.dragKey
    brightnessPanel.dragKey := ''
    row := brightnessPanel.rows[key]
    if row.slider.Enabled
        BrightnessBarAtMouse(key, row.slider, lParam)
    DllCall('user32\ReleaseCapture')
    return 0
}

BrightnessPointerClient(windowHwnd) {
    point := Buffer(8, 0)
    DllCall('user32\GetCursorPos', 'Ptr', point)
    DllCall('user32\ScreenToClient', 'Ptr', windowHwnd, 'Ptr', point)
    return {x: NumGet(point, 0, 'Int'), y: NumGet(point, 4, 'Int')}
}

BrightnessBarAtMouse(key, slider, lParam) {
    point := Buffer(8, 0)
    DllCall('user32\GetCursorPos', 'Ptr', point)
    DllCall('user32\ScreenToClient', 'Ptr', slider.windowHwnd, 'Ptr', point)
    clickX := NumGet(point, 0, 'Int') - slider.x
    value := BrightnessValueAtX(clickX, 7, slider.width - 7, 9)
    if value = slider.Value
        return
    slider.Value := value
    BrightnessSliderChanged(key, slider)
}

BrightnessPanelMouseWheel(wParam, lParam, msg, hwnd) {
    global brightnessPanel
    static remainder := 0
    if !IsObject(brightnessPanel)
        return
    CoordMode('Mouse', 'Screen')
    MouseGetPos(&mx, &my)
    panel := brightnessPanel
    if mx < panel.x || mx >= panel.x + 400
        || my < panel.y || my >= panel.bottom
        return
    delta := (wParam >> 16) & 0xFFFF
    if delta >= 0x8000
        delta -= 0x10000
    remainder += delta
    steps := Floor(Abs(remainder) / 120) * (remainder < 0 ? -1 : 1)
    remainder -= steps * 120
    if steps
        BrightnessWheelAt(panel, mx, my, steps)
    return 0
}

BrightnessWheelAt(panel, mx, my, steps) {
    global availableMonitors
    index := panel.linkedView ? 1 : Max(1, Min(availableMonitors.Length,
        Floor((my - panel.y - 12) / 96) + 1))
    key := availableMonitors[index].key
    slider := panel.rows[key].slider
    if !slider.Enabled
        return
    value := BrightnessClamp(slider.Value + steps)
    if value = slider.Value
        return
    slider.Value := value
    BrightnessSliderChanged(key, slider)
}

class BrightnessBar {
    __New(window, x, y, width, height) {
        this.windowHwnd := window.Hwnd
        this.x := x
        this.y := y
        this.width := width
        this.height := height
        this._value := 0
        this._display := 0
        this._from := 0
        this._started := 0
        this._duration := 150
        this._visible := true
        this.control := window.AddText('x' x ' y' y ' w' width ' h' height
            ' BackgroundTrans +0x100', '')
        this.control.Visible := false
        this.track := window.AddText('x' (x + 11) ' y' (y + 9) ' w' (width - 22)
            ' h6 Background404040', '')
        this.fill := window.AddText('x' (x + 11) ' y' (y + 9)
            ' w1 h6 BackgroundFFFFFF', '')
        this.thumb := window.AddText('x' (x + 6) ' y' (y + 2)
            ' w10 h20 BackgroundE6E6E6', '')
        this.track.Visible := false
        this.fill.Visible := false
        this.thumb.Visible := false
        region := DllCall('gdi32\CreateRoundRectRgn', 'Int', 0, 'Int', 0,
            'Int', 10, 'Int', 20, 'Int', 7, 'Int', 7, 'Ptr')
        DllCall('user32\SetWindowRgn', 'Ptr', this.thumb.Hwnd, 'Ptr', region, 'Int', 1)
        this.Render()
    }
    Hwnd {
        get => this.control.Hwnd
    }
    ContainsHwnd(hwnd) {
        return hwnd = this.control.Hwnd || hwnd = this.track.Hwnd
            || hwnd = this.fill.Hwnd || hwnd = this.thumb.Hwnd
    }
    Value {
        get => this._value
        set {
            value := BrightnessClamp(value)
            if value = this._value
                return
            change := Abs(value - this._value)
            now := A_TickCount
            rapid := this.HasOwnProp('_lastChangeTick')
                && now - this._lastChangeTick < 125
            this._lastChangeTick := now
            this._value := value
            ; Repeated keys and short moves must follow the requested value
            ; immediately. Ease only a standalone, larger jump.
            if change <= 8 || rapid {
                this._display := value
                this._from := value
                this.Render(true)
                return
            }
            this._from := this._display
            this._started := now
            this._duration := 150
            SetTimer(BrightnessAnimateBars, 15)
        }
    }
    Visible {
        get => this._visible
        set {
            this._visible := value
            this.Render(true)
        }
    }
    Move(x, y) {
        if this.x = x && this.y = y
            return
        this.x := x
        this.y := y
        this.Render(true)
    }
    SetImmediate(value) {
        this._value := BrightnessClamp(value)
        this._display := this._value
        this.Render(true)
    }
    SetAnimated(value) {
        value := BrightnessClamp(value)
        if value = this._value && this._display = value
            return
        this._value := value
        this._from := this._display
        this._started := A_TickCount
        this._duration := 220
        SetTimer(BrightnessAnimateBars, 15)
    }
    Step() {
        elapsed := Min(1, Max(0, (A_TickCount - this._started) / this._duration))
        eased := elapsed * elapsed * (3 - 2 * elapsed)
        this._display := this._from + (this._value - this._from) * eased
        this.Render()
        return elapsed < 1
    }
    Enabled {
        get => this.control.Enabled
        set {
            this.control.Enabled := value
            this.thumb.Enabled := value
            this.Render(true)
        }
    }
    Render(force := false) {
        center := Round(11 + (this.width - 22) * this._display / 100)
        previous := this.HasOwnProp('_center') ? this._center : 0
        if !force && previous = center
            return
        bounds := Buffer(16, 0)
        ; The track, fill and thumb are one drawing. Repaint their whole row so
        ; no part of a previous thumb frame survives a clipped paint on Win10/11.
        NumPut('Int', this.x, bounds, 0)
        NumPut('Int', this.y, bounds, 4)
        NumPut('Int', this.x + this.width, bounds, 8)
        NumPut('Int', this.y + this.height, bounds, 12)
        DllCall('user32\RedrawWindow', 'Ptr', this.windowHwnd, 'Ptr', bounds,
            'Ptr', 0, 'UInt', 0x105)
        this._center := center
    }
}

BrightnessAnimateBars(*) {
    global brightnessPanel
    if !IsObject(brightnessPanel) {
        SetTimer(BrightnessAnimateBars, 0)
        return
    }
    active := false
    for _, row in brightnessPanel.rows
        if row.slider._display != row.slider.Value
            active := row.slider.Step() || active
    if !active
        SetTimer(BrightnessAnimateBars, 0)
}

BrightnessDrawShape(dc, left, top, right, bottom, color) {
    brush := DllCall('gdi32\CreateSolidBrush', 'UInt', GdiColor(color), 'Ptr')
    oldBrush := DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', brush, 'Ptr')
    oldPen := DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr',
        DllCall('gdi32\GetStockObject', 'Int', 8, 'Ptr'), 'Ptr')
    DllCall('gdi32\RoundRect', 'Ptr', dc, 'Int', left, 'Int', top,
        'Int', right, 'Int', bottom, 'Int', 14, 'Int', 14)
    DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', oldPen)
    DllCall('gdi32\SelectObject', 'Ptr', dc, 'Ptr', oldBrush)
    DllCall('gdi32\DeleteObject', 'Ptr', brush)
}

BrightnessValueAtX(clickX, channelLeft, channelRight, thumbWidth) {
    minimumCenter := channelLeft + thumbWidth / 2
    maximumCenter := channelRight - thumbWidth / 2
    return maximumCenter <= minimumCenter ? 0
        : BrightnessClamp(100 * (clickX - minimumCenter) / (maximumCenter - minimumCenter))
}

BrightnessFlushSlider(*) {
    global brightnessPending, brightnessValues, brightnessLinked, availableMonitors
    if !brightnessPending.Count
        return
    pending := brightnessPending
    brightnessPending := Map()
    for key, value in pending {
        monitor := BrightnessMonitor(key)
        if !IsObject(monitor)
            continue
        if BrightnessApply(monitor, value)
            continue
        targets := brightnessLinked ? availableMonitors : [monitor]
        for target in targets {
            try brightnessValues[target.key] := BrightnessRead(target)
            catch {
                if brightnessValues.Has(target.key)
                    brightnessValues.Delete(target.key)
            }
        }
        BrightnessRefreshPanel()
    }
}

BrightnessHideCheck(*) {
    global brightnessPanel, brightnessLastActivity, brightnessPointerDown
    if !IsObject(brightnessPanel) {
        SetTimer(BrightnessHideCheck, 0)
        return
    }
    if brightnessPanel.target = 0 || brightnessPanel.progress < 1
        || (brightnessPanel.HasOwnProp('layouting') && brightnessPanel.layouting)
        return
    CoordMode('Mouse', 'Screen')
    MouseGetPos(&mx, &my)
    pressed := GetKeyState('LButton', 'P') || GetKeyState('RButton', 'P')
    if pressed && my >= brightnessPanel.bottom {
        if !brightnessPointerDown && !BrightnessPointerOverSunIcon(mx, my)
            HideBrightnessPanel()
        brightnessPointerDown := pressed
        return
    }
    if mx >= brightnessPanel.x && mx < brightnessPanel.x + 400
        && my >= brightnessPanel.y && my < brightnessPanel.bottom {
        brightnessLastActivity := A_TickCount
        brightnessPointerDown := pressed
        return
    }
    if (pressed && !brightnessPointerDown) || A_TickCount - brightnessLastActivity >= 5000
        HideBrightnessPanel()
    brightnessPointerDown := pressed
}

BrightnessPointerOverSunIcon(x, y) {
    identifier := Buffer(A_PtrSize = 8 ? 40 : 28, 0)
    NumPut('UInt', identifier.Size, identifier, 0)
    NumPut('Ptr', A_ScriptHwnd, identifier, A_PtrSize = 8 ? 8 : 4)
    NumPut('UInt', 2, identifier, A_PtrSize = 8 ? 16 : 8)
    iconRect := Buffer(16, 0)
    if DllCall('shell32\Shell_NotifyIconGetRect', 'Ptr', identifier,
        'Ptr', iconRect, 'Int') != 0
        return false
    return x >= NumGet(iconRect, 0, 'Int') - 3
        && x < NumGet(iconRect, 8, 'Int') + 3
        && y >= NumGet(iconRect, 4, 'Int') - 3
        && y < NumGet(iconRect, 12, 'Int') + 3
}

HideBrightnessPanel(*) {
    global brightnessPanel, uiTest
    SetTimer(BrightnessHideCheck, 0)
    if !IsObject(brightnessPanel)
        return
    BrightnessCancelTypedValue()
    if uiTest
        BrightnessDestroyPanel()
    else
        BrightnessBeginTransition(brightnessPanel, 0)
}

BrightnessBeginTransition(panel, target) {
    if panel.target = target
        return
    panel.progress := BrightnessAnimationProgress(panel)
    panel.from := panel.progress
    panel.target := target
    panel.started := A_TickCount
    panel.duration := Max(80, Round(280 * Abs(target - panel.progress)))
    SetTimer(BrightnessAnimate, 15)
}

BrightnessAnimationProgress(panel) {
    elapsed := Min(1, Max(0, (A_TickCount - panel.started) / panel.duration))
    eased := elapsed * elapsed * (3 - 2 * elapsed)
    return panel.from + (panel.target - panel.from) * eased
}

BrightnessAnimate(*) {
    global brightnessPanel
    if !IsObject(brightnessPanel) {
        SetTimer(BrightnessAnimate, 0)
        return
    }
    panel := brightnessPanel
    progress := BrightnessAnimationProgress(panel)
    panel.progress := progress
    ; Never expose the final one-pixel region along the Windows 11 taskbar.
    if panel.target = 0 && progress <= 0.08 {
        BrightnessDestroyPanel()
        return
    }
    if panel.fadeEntrance
        WinSetTransparent(Max(0, Min(255, Round(255 * progress))),
            'ahk_id ' panel.window.Hwnd)
    else
        BrightnessRenderFrame(panel, progress)
    if A_TickCount - panel.started < panel.duration
        return
    SetTimer(BrightnessAnimate, 0)
    if panel.target = 0
        BrightnessDestroyPanel()
    else if panel.fadeEntrance
        panel.fadeEntrance := false
}

BrightnessRenderFrame(panel, progress) {
    ; Windows will not reveal a window first shown with an empty region.
    visibleHeight := Max(1, Round(panel.height * progress))
    expanding := visibleHeight > (panel.HasOwnProp('paintedHeight') ? panel.paintedHeight : 0)
    panel.y := panel.bottom - visibleHeight
    BrightnessSetVisibleRegion(panel, visibleHeight)
    DllCall('user32\SetWindowPos', 'Ptr', panel.window.Hwnd, 'Ptr', 0,
        'Int', panel.x, 'Int', panel.y, 'Int', 0, 'Int', 0, 'UInt', 0x15)
    if expanding {
        ; Paint the newly exposed dark client area before DWM presents it.
        DllCall('user32\RedrawWindow', 'Ptr', panel.window.Hwnd, 'Ptr', 0,
            'Ptr', 0, 'UInt', 0x85)
    }
    panel.paintedHeight := visibleHeight
}

BrightnessSetVisibleRegion(panel, visibleHeight) {
    region := DllCall('gdi32\CreateRectRgn', 'Int', 0, 'Int', 0,
        'Int', 400, 'Int', visibleHeight, 'Ptr')
    if !region
        return
    if visibleHeight >= 18 {
        ; Extend the rounded region below the visible edge so only the
        ; two upper corners are rounded; the taskbar edge stays square.
        rounded := DllCall('gdi32\CreateRoundRectRgn', 'Int', 0, 'Int', 0,
            'Int', 400, 'Int', visibleHeight + 12, 'Int', 24, 'Int', 24, 'Ptr')
        if rounded {
            DllCall('gdi32\CombineRgn', 'Ptr', region, 'Ptr', region,
                'Ptr', rounded, 'Int', 1)
            DllCall('gdi32\DeleteObject', 'Ptr', rounded)
        }
    }
    if !DllCall('user32\SetWindowRgn', 'Ptr', panel.window.Hwnd,
        'Ptr', region, 'Int', 1)
        DllCall('gdi32\DeleteObject', 'Ptr', region)
}

BrightnessDestroyPanel() {
    global brightnessPanel, brightnessPainting
    if brightnessPainting {
        SetTimer(BrightnessDestroyPanel, -15)
        return
    }
    SetTimer(BrightnessAnimate, 0)
    SetTimer(BrightnessHideCheck, 0)
    SetTimer(BrightnessCommitTypedValue, 0)
    SetTimer(BrightnessTypingCountdown, 0)
    SetTimer(BrightnessAnimateTyping, 0)
    if !IsObject(brightnessPanel)
        return
    panel := brightnessPanel
    brightnessPanel := 0
    panel.window.Hide()
    panel.window.Destroy()
}

OpenBrightnessSettings(*) {
    global brightnessPanel, uiTest
    if uiTest || !IsObject(brightnessPanel) {
        BrightnessDestroyPanel()
        OpenLearning()
        return
    }
    HideBrightnessPanel()
    SetTimer(BrightnessOpenSettingsAfterClose, -300)
}

BrightnessOpenSettingsAfterClose(*) {
    global brightnessPanel
    if IsObject(brightnessPanel) {
        if brightnessPanel.target = 1
            return
        SetTimer(BrightnessOpenSettingsAfterClose, -30)
        return
    }
    OpenLearning()
}

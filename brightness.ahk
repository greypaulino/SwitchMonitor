; VCP 0x10 brightness controls and the separate sun icon/slider panel.

InitBrightness() {
    global selectedMonitor, brightnessSelectedKey, sunIconHandle, sunIconData
    if IsObject(selectedMonitor)
        brightnessSelectedKey := selectedMonitor.key
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
        OnMessage(0x14, BrightnessEraseBackground)
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
    if IsObject(brightnessPanel)
        brightnessPanel.status.Value := failures.Length ? failures[1] : ''
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
        started := A_TickCount
        BrightnessAdjust(direction * BrightnessStep(0))
        nextTick := started + 250
        while GetKeyState(trigger, 'P') {
            now := A_TickCount
            if now >= nextTick {
                duration := now - started
                BrightnessAdjust(direction * BrightnessStep(duration))
                nextTick := now + (duration >= 2000 ? 130 : 190)
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

BrightnessStep(heldMs) {
    return heldMs >= 2000 ? 10 : heldMs >= 250 ? 5 : 1
}

BrightnessClamp(value) {
    return Max(0, Min(100, Round(value)))
}

BrightnessAdjust(amount) {
    global brightnessValues
    monitor := BrightnessActiveMonitor()
    current := brightnessValues.Has(monitor.key) ? brightnessValues[monitor.key] : BrightnessRead(monitor)
    ShowBrightnessPanel()
    BrightnessQueueValue(monitor.key, current + amount)
}

NextBrightnessMonitor(*) {
    global availableMonitors, brightnessSelectedKey, brightnessLastActivity, globalShortcuts, brightnessCaptureActive
    if brightnessCaptureActive
        return
    if availableMonitors.Length < 2
        return
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
    if availableMonitors.Length < 2
        return
    BrightnessFlushSlider()
    brightnessLinked := !brightnessLinked
    IniWrite(brightnessLinked ? '1' : '0', settingsFile, 'Brightness', 'Linked')
    brightnessLastActivity := A_TickCount
    BrightnessRefreshPanel()
}

ShowBrightnessPanel(*) {
    global brightnessPanel, brightnessLastActivity, brightnessPointerDown
    global availableMonitors, brightnessSelectedKey, brightnessValues, uiTest, buttonStyles
    if IsObject(brightnessPanel) {
        brightnessLastActivity := A_TickCount
        BrightnessRefreshPanel()
        BrightnessBeginTransition(brightnessPanel, 1)
        SetTimer(BrightnessHideCheck, 100)
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
    linkButton.OnEvent('Click', ToggleBrightnessLink)
    SetSolidButtonImage(linkButton, A_ScriptDir '\link-white.png', 12, -2, -2)
    linkButton.Visible := availableMonitors.Length > 1
    settingsButton := SolidButton(window, 'x353 y12 w20 h20', '', '3C3C3C')
    settingsButton.OnEvent('Click', OpenBrightnessSettings)
    SetSolidButtonImage(settingsButton, A_ScriptDir '\setting-white.png', 13, -1, -2)
    buttonStyles[settingsButton.Hwnd].iconOpacity := 0.72
    rows := Map()
    for index, monitor in availableMonitors {
        y := 12 + (index - 1) * 96
        dot := window.AddText('x9 y' (y - 1) ' w16 h22 Center c777777', Chr(0x25CF))
        dot.SetFont('s12', 'Segoe UI Symbol')
        dot.Visible := availableMonitors.Length > 1
        dot.OnEvent('Click', SelectBrightnessMonitor.Bind(monitor.key))
        label := window.AddText('x27 y' y ' w142 h22 cFFFFFF +0x200', MonitorLabel(monitor))
        label.OnEvent('Click', SelectBrightnessMonitor.Bind(monitor.key))
        number := window.AddText('x165 y' y ' w70 h20 Center cFFFFFF', '')
        slider := BrightnessBar(window, 16, y + 50, 368, 24)
        try {
            brightnessValues[monitor.key] := BrightnessRead(monitor)
            slider.SetImmediate(brightnessValues[monitor.key])
        } catch {
            slider.Enabled := false
        }
        rows[monitor.key] := {dot: dot, activeDot: false, label: label,
            number: number, slider: slider, name: MonitorLabel(monitor)}
    }
    height := 110 + (availableMonitors.Length - 1) * 96
    status := window.AddText('x16 y' (height - 22) ' w364 h18 cB7B7B7', '')
    brightnessPanel := {window: window, rows: rows, status: status, link: linkButton,
        x: 0, y: 0, bottom: 0, height: height, linkedView: false, updating: false,
        progress: 0, target: 1, from: 0, started: A_TickCount, duration: 280,
        fadeEntrance: !uiTest}
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
    SetTimer(BrightnessHideCheck, 100)
}

BrightnessEraseBackground(dc, lParam, msg, hwnd) {
    global brightnessPanel
    if !IsObject(brightnessPanel)
        return
    if hwnd != brightnessPanel.window.Hwnd
        return
    bounds := Buffer(16, 0)
    DllCall('user32\GetClientRect', 'Ptr', hwnd, 'Ptr', bounds)
    brush := DllCall('gdi32\CreateSolidBrush', 'UInt', GdiColor('1E1E1E'), 'Ptr')
    DllCall('user32\FillRect', 'Ptr', dc, 'Ptr', bounds, 'Ptr', brush)
    DllCall('gdi32\DeleteObject', 'Ptr', brush)
    return 1
}

BrightnessRefreshPanel() {
    global brightnessPanel, brightnessValues, brightnessSelectedKey, brightnessLinked, buttonStyles, availableMonitors
    if !IsObject(brightnessPanel)
        return
    if brightnessPanel.linkedView != (brightnessLinked && availableMonitors.Length > 1)
        BrightnessLayoutPanel(brightnessPanel)
    brightnessPanel.updating := true
    for key, row in brightnessPanel.rows {
        active := availableMonitors.Length > 1
            && (brightnessPanel.linkedView || key = brightnessSelectedKey)
        if row.activeDot != active {
            row.dot.Opt(active ? 'c4EC97B' : 'c777777')
            row.activeDot := active
        }
        if brightnessPanel.linkedView && key != availableMonitors[1].key
            continue
        if brightnessValues.Has(key) {
            label := brightnessValues[key] '%'
            if row.number.Value != label
                row.number.Value := label
            if row.slider.Value != brightnessValues[key]
                row.slider.Value := brightnessValues[key]
        } else
            row.number.Value := 'N/A'
    }
    if buttonStyles.Has(brightnessPanel.link.Hwnd) {
        style := buttonStyles[brightnessPanel.link.Hwnd]
        opacity := brightnessLinked ? 1.0 : 0.42
        changed := style.iconOpacity != opacity
        style.iconOpacity := opacity
        style.iconHoverOpacity := brightnessLinked ? 1.0 : 0.65
        if changed
            DllCall('user32\InvalidateRect', 'Ptr', brightnessPanel.link.Hwnd, 'Ptr', 0, 'Int', 0)
    }
    brightnessPanel.updating := false
}

BrightnessLayoutPanel(panel) {
    global brightnessLinked, availableMonitors
    panel.linkedView := brightnessLinked && availableMonitors.Length > 1
    first := panel.rows[availableMonitors[1].key]
    if panel.linkedView {
        first.slider.Move(16, 54 + 28 * availableMonitors.Length)
    } else {
        first.slider.Move(16, 62)
    }
    for index, monitor in availableMonitors {
        row := panel.rows[monitor.key]
        if panel.linkedView {
            y := 42 + (index - 1) * 28
            row.dot.Move(9, y)
            row.label.Move(27, y, 340, 26)
            row.number.Visible := index = 1
            row.slider.Visible := index = 1
        } else {
            y := 12 + (index - 1) * 96
            row.dot.Move(9, y - 1)
            row.label.Move(27, y, 142, 22)
            row.number.Visible := true
            row.slider.Visible := true
        }
    }
    panel.height := panel.linkedView ? 102 + 28 * availableMonitors.Length
        : 110 + (availableMonitors.Length - 1) * 96
    panel.status.Move(16, panel.height - 22, 364, 18)
    if panel.bottom {
        panel.y := panel.bottom - panel.height
        panel.window.Show('NoActivate x' panel.x ' y' panel.y ' w400 h' panel.height)
        BrightnessRenderFrame(panel, panel.progress)
    }
}

BrightnessSliderChanged(key, control, *) {
    global brightnessPanel
    if !IsObject(brightnessPanel) || brightnessPanel.updating
        return
    BrightnessQueueValue(key, control.Value)
}

BrightnessQueueValue(key, value) {
    global brightnessPanel, brightnessSelectedKey, brightnessLastActivity, brightnessPending, brightnessValues, brightnessLinked, availableMonitors
    value := BrightnessClamp(value)
    brightnessSelectedKey := key
    brightnessValues[key] := value
    if brightnessLinked
        for monitor in availableMonitors
            brightnessValues[monitor.key] := value
    BrightnessRefreshPanel()
    brightnessPending[key] := value
    brightnessLastActivity := A_TickCount
    SetTimer(BrightnessFlushSlider, -80)
}

BrightnessSliderMouseDown(wParam, lParam, msg, hwnd) {
    global brightnessPanel
    if !IsObject(brightnessPanel)
        return
    for key, row in brightnessPanel.rows {
        if row.slider.Hwnd != hwnd
            continue
        if !row.slider.Enabled
            return 0
        DllCall('user32\SetCapture', 'Ptr', hwnd)
        BrightnessBarAtMouse(key, row.slider, lParam)
        return 0
    }
}

BrightnessBarMouseMove(wParam, lParam, msg, hwnd) {
    global brightnessPanel
    if !IsObject(brightnessPanel) || !GetKeyState('LButton', 'P')
        return
    for key, row in brightnessPanel.rows {
        if row.slider.Hwnd != hwnd || !row.slider.Enabled
            continue
        BrightnessBarAtMouse(key, row.slider, lParam)
        return 0
    }
}

BrightnessBarMouseUp(wParam, lParam, msg, hwnd) {
    global brightnessPanel
    if !IsObject(brightnessPanel)
        return
    for key, row in brightnessPanel.rows {
        if row.slider.Hwnd != hwnd
            continue
        if row.slider.Enabled
            BrightnessBarAtMouse(key, row.slider, lParam)
        DllCall('user32\ReleaseCapture')
        return 0
    }
}

BrightnessBarAtMouse(key, slider, lParam) {
    clickX := lParam & 0xFFFF
    if clickX > 32767
        clickX -= 65536
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
        this.track := window.AddText('x' (x + 11) ' y' (y + 9) ' w' (width - 22)
            ' h6 Background404040', '')
        this.fill := window.AddText('x' (x + 11) ' y' (y + 9)
            ' w1 h6 BackgroundFFFFFF', '')
        this.thumb := window.AddText('x' (x + 6) ' y' (y + 2)
            ' w10 h20 BackgroundE6E6E6', '')
        this.control := window.AddText('x' x ' y' y ' w' width ' h' height
            ' BackgroundTrans +0x100', '')
        region := DllCall('gdi32\CreateRoundRectRgn', 'Int', 0, 'Int', 0,
            'Int', 10, 'Int', 20, 'Int', 7, 'Int', 7, 'Ptr')
        DllCall('user32\SetWindowRgn', 'Ptr', this.thumb.Hwnd, 'Ptr', region, 'Int', 1)
        this.Render()
    }
    Hwnd {
        get => this.control.Hwnd
    }
    Value {
        get => this._value
        set {
            value := BrightnessClamp(value)
            if value = this._value
                return
            this._value := value
            this._from := this._display
            this._started := A_TickCount
            SetTimer(BrightnessAnimateBars, 15)
        }
    }
    Visible {
        set {
            this.track.Visible := value
            this.fill.Visible := value
            this.thumb.Visible := value
            this.control.Visible := value
        }
    }
    Move(x, y) {
        if this.x = x && this.y = y
            return
        this.x := x
        this.y := y
        this.track.Move(x + 11, y + 9)
        this.fill.Move(x + 11, y + 9)
        this.thumb.Move(x + 6, y + 2)
        this.control.Move(x, y)
        this.Render(true)
    }
    SetImmediate(value) {
        this._value := BrightnessClamp(value)
        this._display := this._value
        this.Render(true)
    }
    Step() {
        elapsed := Min(1, Max(0, (A_TickCount - this._started) / 150))
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
        DllCall('user32\SetWindowPos', 'Ptr', this.fill.Hwnd, 'Ptr', 0,
            'Int', this.x + 11, 'Int', this.y + 9, 'Int', Max(1, center - 11),
            'Int', 6, 'UInt', 0x1C)
        DllCall('user32\SetWindowPos', 'Ptr', this.thumb.Hwnd, 'Ptr', 0,
            'Int', this.x + center - 5, 'Int', this.y + 2,
            'Int', 10, 'Int', 20, 'UInt', 0x1C)
        bounds := Buffer(16, 0)
        left := previous ? this.x + Min(previous, center) - 7 : this.x
        right := previous ? this.x + Max(previous, center) + 7 : this.x + this.width
        NumPut('Int', left, bounds, 0)
        NumPut('Int', this.y, bounds, 4)
        NumPut('Int', right, bounds, 8)
        NumPut('Int', this.y + this.height, bounds, 12)
        ; Erase the traveled strip with the panel's dark WM_ERASEBKGND handler.
        ; Moving both child controls without an intermediate redraw avoids flicker.
        DllCall('user32\RedrawWindow', 'Ptr', this.windowHwnd, 'Ptr', bounds,
            'Ptr', 0, 'UInt', 0x185)
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
        'Int', right, 'Int', bottom, 'Int', 6, 'Int', 6)
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
        return
    CoordMode('Mouse', 'Screen')
    MouseGetPos(&mx, &my)
    pressed := GetKeyState('LButton', 'P') || GetKeyState('RButton', 'P')
    if pressed && my >= brightnessPanel.bottom {
        brightnessLastActivity := A_TickCount
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

HideBrightnessPanel(*) {
    global brightnessPanel, uiTest
    SetTimer(BrightnessHideCheck, 0)
    if !IsObject(brightnessPanel)
        return
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
    region := DllCall('gdi32\CreateRectRgn', 'Int', 0, 'Int', 0,
        'Int', 400, 'Int', visibleHeight, 'Ptr')
    if region && !DllCall('user32\SetWindowRgn', 'Ptr', panel.window.Hwnd,
        'Ptr', region, 'Int', 1)
        DllCall('gdi32\DeleteObject', 'Ptr', region)
    DllCall('user32\SetWindowPos', 'Ptr', panel.window.Hwnd, 'Ptr', 0,
        'Int', panel.x, 'Int', panel.y, 'Int', 0, 'Int', 0, 'UInt', 0x15)
    if expanding {
        ; Paint the newly exposed dark client area before DWM presents it.
        DllCall('user32\RedrawWindow', 'Ptr', panel.window.Hwnd, 'Ptr', 0,
            'Ptr', 0, 'UInt', 0x85)
    }
    panel.paintedHeight := visibleHeight
}

BrightnessDestroyPanel() {
    global brightnessPanel
    SetTimer(BrightnessAnimate, 0)
    SetTimer(BrightnessHideCheck, 0)
    if !IsObject(brightnessPanel)
        return
    panel := brightnessPanel
    brightnessPanel := 0
    panel.window.Hide()
    panel.window.Destroy()
}

OpenBrightnessSettings(*) {
    BrightnessDestroyPanel()
    OpenLearning()
}

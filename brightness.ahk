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
        OnExit(BrightnessCleanup)
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
    value := UsesIntelLg(monitor)
        ? IntelLegacyDdc(monitor.device, false).GetVcp(0x10)
        : RunWait('"' monitorTool '" /GetValue "' monitor.target '" 10', , 'Hide')
    if value < 0 || value > 100
        throw Error('Brightness reading is unavailable for ' MonitorLabel(monitor) '.')
    return value
}

BrightnessWrite(monitor, value) {
    global monitorTool, uiTest
    value := BrightnessClamp(value)
    if uiTest
        return
    if UsesIntelLg(monitor)
        IntelLegacyDdc(monitor.device, false).SetVcp(0x10, value)
    else if RunWait('"' monitorTool '" /SetValue "' monitor.target '" 10 ' value, , 'Hide') != 0
        throw Error('Brightness command failed for ' MonitorLabel(monitor) '.')
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
    BrightnessApply(monitor, current + amount)
    ShowBrightnessPanel()
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
    global brightnessLinked, settingsFile, brightnessLastActivity
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
    window.SetFont('s10 cFFFFFF', 'Segoe UI')
    linkButton := SolidButton(window, 'x325 y12 w20 h20', '', '2D2D2D')
    linkButton.OnEvent('Click', ToggleBrightnessLink)
    SetSolidButtonImage(linkButton, A_ScriptDir '\link-white.png', 12, -2, -2)
    settingsButton := SolidButton(window, 'x353 y12 w20 h20', '', '3C3C3C')
    settingsButton.OnEvent('Click', OpenBrightnessSettings)
    SetSolidButtonImage(settingsButton, A_ScriptDir '\setting-white.png', 13, -1, -2)
    buttonStyles[settingsButton.Hwnd].iconOpacity := 0.72
    rows := Map()
    for index, monitor in availableMonitors {
        y := 12 + (index - 1) * 72
        label := window.AddText('x27 y' y ' w142 h20 cFFFFFF', MonitorLabel(monitor))
        label.OnEvent('Click', SelectBrightnessMonitor.Bind(monitor.key))
        number := window.AddText('x165 y' y ' w70 h20 Center cFFFFFF', '')
        slider := BrightnessBar(window, 16, y + 50, 368, 24)
        try {
            brightnessValues[monitor.key] := BrightnessRead(monitor)
            slider.Value := brightnessValues[monitor.key]
        } catch {
            slider.Enabled := false
        }
        rows[monitor.key] := {label: label, number: number, slider: slider}
    }
    height := 110 + (availableMonitors.Length - 1) * 72
    status := window.AddText('x16 y' (height - 22) ' w364 h18 cB7B7B7', '')
    brightnessPanel := {window: window, rows: rows, status: status, link: linkButton,
        x: 0, y: 0, bottom: 0, height: height, updating: false,
        progress: 0, target: 1, from: 0, started: A_TickCount, duration: 280}
    window.OnEvent('Close', HideBrightnessPanel)
    BrightnessRefreshPanel()
    MonitorGetWorkArea(MonitorGetPrimary(), &left, &top, &right, &bottom)
    x := Max(left + 8, right - 408)
    y := Max(top, bottom - height)
    brightnessPanel.x := x
    brightnessPanel.y := uiTest ? y : bottom
    brightnessPanel.bottom := bottom
    window.Show('Hide x' x ' y' brightnessPanel.y ' w400 h' height)
    ApplyDarkWindow(window)
    if uiTest {
        brightnessPanel.progress := 1
        window.Show('NoActivate x' x ' y' y)
    } else {
        BrightnessRenderFrame(brightnessPanel, 0)
        window.Show('NoActivate')
        SetTimer(BrightnessAnimate, 15)
    }
    brightnessLastActivity := A_TickCount
    brightnessPointerDown := GetKeyState('LButton', 'P') || GetKeyState('RButton', 'P')
    SetTimer(BrightnessHideCheck, 100)
}

BrightnessRefreshPanel() {
    global brightnessPanel, brightnessValues, brightnessSelectedKey, brightnessLinked, buttonStyles
    if !IsObject(brightnessPanel)
        return
    brightnessPanel.updating := true
    for key, row in brightnessPanel.rows {
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

BrightnessSliderChanged(key, control, *) {
    global brightnessPanel, brightnessSelectedKey, brightnessLastActivity, brightnessPending, brightnessValues
    if !IsObject(brightnessPanel) || brightnessPanel.updating
        return
    brightnessSelectedKey := key
    brightnessValues[key] := control.Value
    brightnessPanel.rows[key].number.Value := control.Value '%'
    brightnessPending[key] := control.Value
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
    index := Max(1, Min(availableMonitors.Length,
        Floor((my - panel.y - 12) / 72) + 1))
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
            this.Render()
        }
    }
    Enabled {
        get => this.control.Enabled
        set {
            this.control.Enabled := value
            this.thumb.Enabled := value
            this.Render()
        }
    }
    Render() {
        center := Round(11 + (this.width - 22) * this.Value / 100)
        previous := this.HasOwnProp('_center') ? this._center : 0
        this.fill.Move(this.x + 11, this.y + 9, Max(1, center - 11), 6)
        this.thumb.Move(this.x + center - 5, this.y + 2, 10, 20)
        bounds := Buffer(16, 0)
        left := previous ? this.x + Min(previous, center) - 7 : this.x
        right := previous ? this.x + Max(previous, center) + 7 : this.x + this.width
        NumPut('Int', left, bounds, 0)
        NumPut('Int', this.y, bounds, 4)
        NumPut('Int', right, bounds, 8)
        NumPut('Int', this.y + this.height, bounds, 12)
        DllCall('user32\RedrawWindow', 'Ptr', this.windowHwnd, 'Ptr', bounds,
            'Ptr', 0, 'UInt', 0x385)
        this._center := center
    }
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
    BrightnessRenderFrame(panel, progress)
    if A_TickCount - panel.started < panel.duration
        return
    SetTimer(BrightnessAnimate, 0)
    if panel.target = 0
        BrightnessDestroyPanel()
}

BrightnessRenderFrame(panel, progress) {
    ; Windows will not reveal a window first shown with an empty region.
    visibleHeight := Max(1, Round(panel.height * progress))
    panel.y := panel.bottom - visibleHeight
    region := DllCall('gdi32\CreateRectRgn', 'Int', 0, 'Int', 0,
        'Int', 400, 'Int', visibleHeight, 'Ptr')
    if region && !DllCall('user32\SetWindowRgn', 'Ptr', panel.window.Hwnd,
        'Ptr', region, 'Int', 1)
        DllCall('gdi32\DeleteObject', 'Ptr', region)
    DllCall('user32\SetWindowPos', 'Ptr', panel.window.Hwnd, 'Ptr', 0,
        'Int', panel.x, 'Int', panel.y, 'Int', 0, 'Int', 0, 'UInt', 0x15)
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

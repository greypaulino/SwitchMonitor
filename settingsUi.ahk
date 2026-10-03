; Settings uses the same name/shortcut rows as the Shortcuts window.
OpenLearning(*) {
    global selectedMonitor, wizardOpen, busy, settingsFile, uiTest, availableMonitors, profileError, globalShortcuts, brightnessLinked
    if wizardOpen || busy
        return
    try RefreshAvailableMonitors()
    catch as err {
        if !uiTest
            MsgBox(err.Message, 'Monitors', 'Iconx')
        return
    }
    wizardOpen := true
    state := {index: 1, rows: [], drafts: Map(), baselines: Map(), loading: false, capturing: false,
        closing: false, closePending: false,
        globals: Map('cycle', globalShortcuts['cycle'], 'settings', globalShortcuts['settings'],
            'brightnessDown', globalShortcuts['brightnessDown'], 'brightnessUp', globalShortcuts['brightnessUp'],
            'brightnessNext', globalShortcuts['brightnessNext']),
        originalGlobals: Map('cycle', globalShortcuts['cycle'], 'settings', globalShortcuts['settings'],
            'brightnessDown', globalShortcuts['brightnessDown'], 'brightnessUp', globalShortcuts['brightnessUp'],
            'brightnessNext', globalShortcuts['brightnessNext']),
        linked: brightnessLinked, originalLinked: brightnessLinked,
        mainDisplayName: BrightnessMainDisplayName(),
        originalMainDisplayName: BrightnessMainDisplayName(),
        selected: 0, refresh: false, hover: '', mock: 'valid'}
    labels := []
    for index, monitor in availableMonitors {
        labels.Push(MonitorLabel(monitor) (availableMonitors.Length > 1 ? ' / Display ' index : ''))
        if IsObject(selectedMonitor) && monitor.key = selectedMonitor.key
            state.index := index
    }
    ui := {rows: [], shown: false, height: 934}
    window := Gui(, 'SwitchMonitor Settings')
    window.BackColor := '1E1E1E'
    window.SetFont('s10 cD4D4D4', 'Segoe UI')
    window.AddText('x40 y32 w480 h38 Center cFFFFFF', 'Settings').SetFont('s22 Bold')
    window.AddText('x40 y88 w480 h20 Center cB7B7B7', 'Choose inputs and keyboard shortcuts.').SetFont('s9')
    window.AddText('x40 y128 w72 h25 cFFFFFF', 'Monitor:').SetFont('s10 Bold')
    ui.choice := window.AddDropDownList('x120 y123 w165 Choose' state.index, labels)
    ui.choice.Enabled := availableMonitors.Length > 1
    if availableMonitors.Length = 1 {
        ui.choice.Visible := false
        window.AddText('x120 y123 w165 h31 Background2D2D2D cFFFFFF +0x200', '  ' labels[1])
    }
    ui.choice.OnEvent('Change', ChooseMonitor)
    ui.mainDisplay := window.AddCheckBox('x299 y127 w124 h24 cFFFFFF', 'Main display')
    ui.mainDisplay.OnEvent('Click', SetMainDisplayDraft)
    ui.refresh := SolidButton(window, 'x430 y123 w90 h31', 'Detect', '3C3C3C')
    ui.refresh.OnEvent('Click', Reload)
    window.AddText('x40 y167 w480 h1 Background3C3C3C')
    ui.inputTitle := window.AddText('x40 y185 w480 h23 Center cFFFFFF', 'INPUT SHORTCUTS')
    ui.inputTitle.SetFont('s11 Bold')
    ui.inputHint := window.AddText('x40 y210 w480 h18 Center cB7B7B7', 'Check the inputs to include in Next input.')
    ui.inputHint.SetFont('s9')
    ui.noInputs := window.AddText('x68 y242 w447 h42 cB7B7B7', 'This built-in display has no switchable inputs. Use the brightness controls below.')
    ui.noInputs.Visible := false
    ui.globalLine := window.AddText('x40 y376 w480 h1 Background3C3C3C')
    ui.cycleName := window.AddText('x68 y394 w160 h32 cFFFFFF +0x200', 'Next input')
    ui.cycleButton := SolidButton(window, 'x240 y394 w275 h32', ShortcutLabel(state.globals['cycle']), '2D2D2D')
    ui.cycleButton.OnEvent('Click', CaptureClick.Bind('cycle', 0))
    ui.settingsName := window.AddText('x68 y440 w160 h32 cFFFFFF +0x200', 'Open Settings')
    ui.settingsButton := SolidButton(window, 'x240 y440 w275 h32', ShortcutLabel(state.globals['settings']), '2D2D2D')
    ui.settingsButton.OnEvent('Click', CaptureClick.Bind('settings', 0))
    ui.brightnessLine := window.AddText('x40 y490 w480 h1 Background3C3C3C')
    ui.brightnessTitle := window.AddText('x40 y508 w480 h23 Center cFFFFFF', 'BRIGHTNESS CONTROL')
    ui.brightnessTitle.SetFont('s11 Bold')
    ui.downName := window.AddText('x68 y546 w160 h32 cFFFFFF +0x200', 'Decrease brightness')
    ui.downButton := SolidButton(window, 'x240 y546 w275 h32', ShortcutLabel(state.globals['brightnessDown']), '2D2D2D')
    ui.downButton.OnEvent('Click', CaptureClick.Bind('brightnessDown', 0))
    ui.upName := window.AddText('x68 y590 w160 h32 cFFFFFF +0x200', 'Increase brightness')
    ui.upButton := SolidButton(window, 'x240 y590 w275 h32', ShortcutLabel(state.globals['brightnessUp']), '2D2D2D')
    ui.upButton.OnEvent('Click', CaptureClick.Bind('brightnessUp', 0))
    ui.nextName := window.AddText('x68 y634 w160 h32 cFFFFFF +0x200', 'Next monitor')
    ui.nextButton := SolidButton(window, 'x240 y634 w275 h32', ShortcutLabel(state.globals['brightnessNext']), '2D2D2D')
    ui.nextButton.OnEvent('Click', CaptureClick.Bind('brightnessNext', 0))
    ui.linkName := window.AddText('x68 y678 w160 h32 cFFFFFF +0x200', 'Link monitor brightness')
    ui.linkButton := SolidButton(window, 'x240 y678 w275 h32', state.linked ? 'Linked' : 'Independent', '2D2D2D')
    ui.linkButton.OnEvent('Click', ToggleLink)
    ui.linkButton.Enabled := availableMonitors.Length > 1
    if availableMonitors.Length = 1
        ui.linkName.SetFont('c808080')
    ui.actionLine := window.AddText('x40 y728 w480 h1 Background3C3C3C')
    ui.check := SolidButton(window, 'x120 y746 w154 h36', 'Check connection', '3C3C3C')
    ui.check.SetFont('s9 Bold')
    ui.check.OnEvent('Click', CheckControl)
    ui.export := SolidButton(window, 'x286 y746 w154 h36', 'Export diagnostics', '3C3C3C')
    ui.export.SetFont('s9 Bold')
    ui.export.OnEvent('Click', ExportDiagnostic)
    ui.backup := SolidButton(window, 'x120 y790 w154 h36', 'Back up settings', '3C3C3C')
    ui.backup.SetFont('s9 Bold')
    ui.backup.OnEvent('Click', BackupConfig)
    ui.restore := SolidButton(window, 'x286 y790 w154 h36', 'Restore settings', '3C3C3C')
    ui.restore.SetFont('s9 Bold')
    ui.restore.OnEvent('Click', RestoreConfig)
    ui.status := window.AddText('x40 y838 w480 h31 Center cB7B7B7', '')
    ui.status.SetFont('s9')
    ui.bottomLine := window.AddText('x40 y872 w480 h1 Background3C3C3C')
    ui.close := SolidButton(window, 'x205 y886 w150 h38', 'Close', '3C3C3C')
    ui.close.OnEvent('Click', Close)
    ui.cancel := SolidButton(window, 'x120 y886 w150 h38', 'Cancel', '3C3C3C')
    ui.cancel.OnEvent('Click', Close)
    ui.save := SolidButton(window, 'x290 y886 w150 h38', 'Save')
    ui.save.SetFont('s9 Bold')
    ui.save.OnEvent('Click', Commit)
    window.OnEvent('Close', Close)
    window.OnEvent('Escape', Close)
    LoadMonitor()
    RoundControls([{control: ui.refresh, width: 90, height: 31},
        {control: ui.cycleButton, width: 275, height: 32}, {control: ui.settingsButton, width: 275, height: 32},
        {control: ui.downButton, width: 275, height: 32}, {control: ui.upButton, width: 275, height: 32},
        {control: ui.nextButton, width: 275, height: 32}, {control: ui.linkButton, width: 275, height: 32},
        {control: ui.check, width: 154, height: 36},
        {control: ui.export, width: 154, height: 36},
        {control: ui.backup, width: 154, height: 36}, {control: ui.restore, width: 154, height: 36},
        {control: ui.close, width: 150, height: 38},
        {control: ui.cancel, width: 150, height: 38},
        {control: ui.save, width: 150, height: 38}])
    ShowSmooth(window, 'w560 h' ui.height)
    ui.shown := true
    if uiTest {
        ; A close request during detection must leave row controls alive until loading ends.
        state.loading := true
        Close()
        if !DllCall('user32\IsWindow', 'Ptr', window.Hwnd)
            throw Error('Settings closed while monitor rows were loading.')
        state.loading := false
        state.closePending := false
        buttonStyle := DllCall('user32\GetWindowLongPtrW', 'Ptr', ui.cycleButton.Hwnd, 'Int', -16, 'Ptr')
        if (buttonStyle & 0xF) != 0xB
            throw Error('Shortcut button is not owner-drawn.')
        if availableMonitors.Length = 1 {
            if ui.choice.Enabled || ui.rows.Length != state.rows.Length || ui.linkButton.Enabled
                throw Error('Single-monitor settings did not render its input rows.')
            if ui.mainDisplay.Enabled
                throw Error('Single-monitor settings enabled the main-display choice.')
            Close()
            return
        }
        if !ui.choice.Enabled || state.rows.Length != 4 || ui.rows.Length != 4
            throw Error('Multi-monitor settings did not render its input rows.')
        if !ui.close.Visible || ui.cancel.Visible || ui.save.Visible
            throw Error('Clean settings should show only Close.')
        if ui.mainDisplay.Enabled != BrightnessCanBeMainDisplay(availableMonitors[state.index])
            throw Error('Main-display choice does not match Windows display availability.')
        SelectRow(1)
        ApplyPortShortcut(1, 'Ctrl|Alt|3')
        if state.rows[1].shortcut != 'Ctrl|Alt|3' || state.rows[3].shortcut != 'Ctrl|Alt|1'
            throw Error('Shortcut reassignment failed.')
        if ui.close.Visible || !ui.cancel.Visible || !ui.save.Visible
            throw Error('Modified settings should show Cancel and Save.')
        started := A_TickCount
        CaptureClick('port', 1)
        if state.rows[1].shortcut != 'Ctrl|Alt|K' || A_TickCount - started < 2900
            throw Error('Three-second shortcut capture failed.')
        state.mock := 'none'
        CaptureClick('port', 1)
        if state.rows[1].shortcut != 'Ctrl|Alt|K'
            throw Error('An empty capture changed the previous shortcut.')
        state.mock := 'escape'
        CaptureClick('port', 1)
        if state.rows[1].shortcut != ''
            throw Error('Escape did not clear the shortcut.')
        ApplyPortShortcut(1, 'Ctrl|Alt|K')
        state.mock := 'delete'
        CaptureClick('cycle', 0)
        if state.globals['cycle'] != ''
            throw Error('Delete did not clear the global shortcut.')
        ApplyGlobalShortcut('cycle', 'Ctrl|Alt|M')
        state.mock := 'valid'
        ui.choice.Choose(2)
        ChooseMonitor()
        ui.choice.Choose(1)
        ChooseMonitor()
        if state.rows[1].shortcut != 'Ctrl|Alt|K'
            throw Error('Draft changes were lost when changing monitors.')
        for candidateIndex, candidate in availableMonitors {
            candidateName := BrightnessDisplayName(candidate)
            if !BrightnessCanBeMainDisplay(candidate)
                || StrUpper(candidateName) = StrUpper(state.originalMainDisplayName)
                continue
            ui.choice.Choose(candidateIndex)
            ChooseMonitor()
            ui.mainDisplay.Value := 1
            SetMainDisplayDraft()
            if StrUpper(state.mainDisplayName) != StrUpper(candidateName)
                || !ui.save.Visible
                throw Error('Main-display checkbox did not create a single pending choice.')
            ui.choice.Choose(1)
            ChooseMonitor()
            if ui.mainDisplay.Value != (StrUpper(BrightnessDisplayName(availableMonitors[1]))
                = StrUpper(candidateName))
                throw Error('Main-display checkbox did not follow the selected monitor.')
            state.mainDisplayName := state.originalMainDisplayName
            UpdateMainDisplayCheck()
            RefreshActions()
            break
        }
        ui.rows[1].check.Value := 1
        CheckConnected(1, ui.rows[1].check)
        ui.rows[2].check.Value := 0
        CheckConnected(2, ui.rows[2].check)
        ui.rows[3].check.Value := 1
        CheckConnected(3, ui.rows[3].check)
        if !ApplyGlobalShortcut('brightnessUp', 'Ctrl|Alt|F9')
            throw Error('Brightness shortcut could not be changed in Settings.')
        ToggleLink()
        ; Global brightness settings must save even when the selected display
        ; has no input rows, as on a built-in laptop screen.
        state.rows := []
        Commit()
    } else {
        SetTimer(HoverGlobals, 80)
    }

    LoadMonitor() {
        if state.closing
            return
        state.loading := true
        ui.choice.Enabled := false
        ui.refresh.Enabled := false
        state.selected := 0
        try {
            monitor := availableMonitors[state.index]
            if state.drafts.Has(monitor.key) {
                state.rows := state.drafts[monitor.key]
                ui.status.Value := 'Unsaved changes. Choose Save to keep them.'
            } else {
                saved := LoadAssignments(settingsFile, monitor.key)
                found := DiscoverInputs(monitor, saved, state.refresh)
                state.rows := MakeRows(found.values, saved, IsLg29wk600(monitor) || IsInternalDisplay(monitor))
                AvoidDefaultShortcutConflicts(state.rows, saved, monitor, state.drafts, availableMonitors, settingsFile)
                state.drafts[monitor.key] := state.rows
                if !state.baselines.Has(monitor.key)
                    state.baselines[monitor.key] := saved.Count ? RowsSnapshot(state.rows) : ''
                ui.status.Value := IsLg29wk600(monitor) ? CompatibilityInfo(monitor).message : found.message
            }
            RenderRows()
            if !state.baselines.Has(monitor.key)
                state.baselines[monitor.key] := RowsSnapshot(state.rows)
            if profileError != ''
                ui.status.Value := 'Resolve shortcut conflict: ' profileError
        } catch as err {
            state.rows := []
            RenderRows()
            ui.status.Value := err.Message
        } finally {
            state.loading := false
            state.refresh := false
            ui.choice.Enabled := availableMonitors.Length > 1
            ui.refresh.Enabled := !IsInternalDisplay(availableMonitors[state.index])
            UpdateMainDisplayCheck()
            RefreshActions()
            if state.closePending {
                state.closePending := false
                Close()
            }
        }
    }
    RenderRows() {
        if state.closing
            return
        for row in ui.rows {
            row.check.Visible := false
            row.name.Visible := false
            row.button.Visible := false
        }
        ui.rows := []
        internal := IsInternalDisplay(availableMonitors[state.index])
        ui.inputTitle.Value := internal ? 'BUILT-IN DISPLAY' : 'INPUT SHORTCUTS'
        ui.inputHint.Visible := !internal
        ui.noInputs.Visible := internal
        ui.refresh.Visible := !internal
        for index, entry in state.rows {
            y := 236 + (index - 1) * 44
            check := window.AddCheckBox('x42 y' (y + 7) ' w22 h23', '')
            check.Value := entry.connected
            check.OnEvent('Click', CheckConnected.Bind(index))
            name := window.AddText('x68 y' y ' w160 h32 cFFFFFF +0x200', entry.name)
            name.OnEvent('Click', SelectRow.Bind(index))
            button := SolidButton(window, 'x240 y' y ' w275 h32', ShortcutLabel(entry.shortcut), '2D2D2D')
            button.OnEvent('Click', CaptureClick.Bind('port', index))
            ui.rows.Push({check: check, name: name, button: button})
            RoundControls([{control: button, width: 275, height: 32}])
        }
        offset := internal ? -80 : Max(0, state.rows.Length - 3) * 44
        ui.globalLine.Move(, 376 + offset)
        ui.cycleName.Move(, 394 + offset)
        ui.cycleButton.Move(, 394 + offset)
        ui.settingsName.Move(, 440 + offset)
        ui.settingsButton.Move(, 440 + offset)
        ui.brightnessLine.Move(, 490 + offset)
        ui.brightnessTitle.Move(, 508 + offset)
        ui.downName.Move(, 546 + offset)
        ui.downButton.Move(, 546 + offset)
        ui.upName.Move(, 590 + offset)
        ui.upButton.Move(, 590 + offset)
        ui.nextName.Move(, 634 + offset)
        ui.nextButton.Move(, 634 + offset)
        ui.linkName.Move(, 678 + offset)
        ui.linkButton.Move(, 678 + offset)
        ui.actionLine.Move(, 728 + offset)
        ui.check.Move(, 746 + offset)
        ui.export.Move(, 746 + offset)
        ui.backup.Move(, 790 + offset)
        ui.restore.Move(, 790 + offset)
        ui.status.Move(, 838 + offset)
        ui.bottomLine.Move(, 872 + offset)
        ui.close.Move(, 886 + offset)
        ui.cancel.Move(, 886 + offset)
        ui.save.Move(, 886 + offset)
        ui.height := 934 + offset
        if ui.shown {
            window.Show('h' ui.height)
            ApplyDarkWindow(window)
        }
    }
    SelectRow(index, *) {
        if index < 1 || index > state.rows.Length
            return
        state.selected := index
        ui.status.Value := 'Selected: ' state.rows[index].name
    }
    CheckConnected(index, control, *) {
        if state.loading || state.capturing
            return
        state.rows[index].connected := !!control.Value
        SelectRow(index)
        ui.status.Value := 'Connected inputs updated. Save to apply the cycle.'
        RefreshActions()
    }
    RowsSnapshot(rows) {
        snapshot := ''
        for row in rows
            snapshot .= row.code ':' row.name ':' (row.connected ? 1 : 0) ':' row.shortcut '`n'
        return snapshot
    }
    RefreshActions() {
        dirty := state.linked != state.originalLinked
            || StrUpper(state.mainDisplayName) != StrUpper(state.originalMainDisplayName)
        for kind, chord in state.globals
            if chord != state.originalGlobals[kind]
                dirty := true
        if !dirty
            for key, rows in state.drafts
                if state.baselines.Has(key) && RowsSnapshot(rows) != state.baselines[key] {
                    dirty := true
                    break
                }
        ui.close.Visible := !dirty
        ui.cancel.Visible := dirty
        ui.save.Visible := dirty
        ui.save.Enabled := dirty
    }
    ChooseMonitor(*) {
        if state.loading || state.capturing
            return
        state.index := ui.choice.Value
        SelectActiveMonitor(state.index)
        LoadMonitor()
    }
    UpdateMainDisplayCheck() {
        monitor := availableMonitors[state.index]
        displayName := BrightnessDisplayName(monitor)
        ui.mainDisplay.Enabled := BrightnessCanBeMainDisplay(monitor)
        ui.mainDisplay.Value := displayName != ''
            && StrUpper(displayName) = StrUpper(state.mainDisplayName)
    }
    SetMainDisplayDraft(*) {
        if state.loading || state.capturing
            return
        monitor := availableMonitors[state.index]
        if !BrightnessCanBeMainDisplay(monitor) {
            UpdateMainDisplayCheck()
            return
        }
        if !ui.mainDisplay.Value {
            ui.mainDisplay.Value := 1
            ui.status.Value := 'Choose another monitor to change the main display.'
            return
        }
        state.mainDisplayName := BrightnessDisplayName(monitor)
        ui.status.Value := 'Main display selection will apply when you save.'
        RefreshActions()
    }
    Reload(*) {
        if state.loading || state.capturing
            return
        key := availableMonitors[state.index].key
        if state.drafts.Has(key)
            state.drafts.Delete(key)
        state.refresh := true
        LoadMonitor()
    }
    CaptureClick(kind, index, *) {
        if state.loading || state.capturing
            return
        if kind = 'port'
            SelectRow(index)
        CaptureShortcut(kind, index)
    }
    CaptureShortcut(kind, index) {
        global brightnessCaptureActive
        state.capturing := true
        brightnessCaptureActive := true
        ui.choice.Enabled := false
        ui.refresh.Enabled := false
        ui.save.Enabled := false
        recording := {held: Map(), candidate: '', clear: false, cancel: false, tooMany: false, peak: 0}
        dialog := Gui('+Owner' window.Hwnd, 'Record shortcut')
        dialog.BackColor := '1E1E1E'
        dialog.SetFont('s10 cFFFFFF', 'Segoe UI')
        dialog.AddText('x24 y18 w350 h30 Center cFFFFFF', 'Press a shortcut now').SetFont('s16 Bold')
        dialog.AddText('x24 y55 w350 h43 Center cB7B7B7', 'Hold 2 to 4 keys within 3 seconds.`nEsc or Delete clears the shortcut.')
        timerLabel := dialog.AddText('x24 y111 w350 h35 Center cFFFFFF', '3 seconds remaining')
        timerLabel.SetFont('s11 Bold')
        hook := InputHook('T3')
        hook.KeyOpt('{All}', 'NS')
        hook.OnKeyDown := RecordDown
        hook.OnKeyUp := RecordUp
        dialog.OnEvent('Close', CancelDialog)
        dialog.OnEvent('Escape', ClearDialog)
        ShowSmooth(dialog, 'w398 h165')
        started := A_TickCount
        SetTimer(Countdown, 100)
        try {
            hook.Start()
            if uiTest {
                if state.mock = 'escape'
                    RecordDown(hook, 0x1B, 0x001)
                else if state.mock = 'delete'
                    RecordDown(hook, 0x2E, 0x153)
                else if state.mock = 'valid' {
                    RecordDown(hook, 0xA2, 0x01D)
                    RecordDown(hook, 0xA4, 0x038)
                    RecordDown(hook, 0x4B, 0x025)
                }
            }
            hook.Wait()
            if recording.clear {
                accepted := kind = 'port' ? ApplyPortShortcut(index, '') : ApplyGlobalShortcut(kind, '')
                if accepted
                    ui.status.Value := 'Shortcut cleared. Choose Save.'
            } else if recording.cancel || recording.candidate = '' {
                ui.status.Value := 'No shortcut selected. Previous assignment kept.'
            } else if recording.tooMany {
                ui.status.Value := 'Use no more than 4 keys. Previous assignment kept.'
            } else {
                accepted := kind = 'port' ? ApplyPortShortcut(index, recording.candidate)
                    : ApplyGlobalShortcut(kind, recording.candidate)
                if accepted
                    ui.status.Value := 'Shortcut updated. Choose Save.'
            }
        } catch as err {
            ui.status.Value := err.Message
            if uiTest
                throw err
        } finally {
            hook.Stop()
            SetTimer(Countdown, 0)
            CloseSmooth(dialog)
            state.capturing := false
            brightnessCaptureActive := false
            ui.choice.Enabled := availableMonitors.Length > 1
            ui.refresh.Enabled := true
            RefreshActions()
        }
        RecordDown(input, vk, sc) {
            if vk = 0x1B || vk = 0x2E {
                recording.clear := true
                hook.Stop()
                return
            }
            raw := Format('vk{:02X}sc{:03X}', vk, sc)
            if recording.held.Has(raw)
                return
            recording.held[raw] := NormalizeKey(GetKeyName(raw))
            keys := []
            for physical, name in recording.held
                if !HasKeyToken(keys, name)
                    keys.Push(name)
            if keys.Length > 4 {
                recording.tooMany := true
                return
            }
            if keys.Length >= 2 && keys.Length >= recording.peak {
                chord := ''
                for name in keys
                    chord .= (chord = '' ? '' : '|') name
                recording.candidate := chord
                recording.peak := keys.Length
            }
        }
        RecordUp(input, vk, sc) {
            raw := Format('vk{:02X}sc{:03X}', vk, sc)
            if recording.held.Has(raw)
                recording.held.Delete(raw)
            if !recording.held.Count
                recording.peak := 0
        }
        Countdown() {
            seconds := Max(0, Ceil((3000 - (A_TickCount - started)) / 1000))
            timerLabel.Value := recording.candidate = '' ? seconds ' seconds remaining'
                : ShortcutLabel(recording.candidate) '  ·  ' seconds ' s'
        }
        CancelDialog(*) {
            recording.cancel := true
            hook.Stop()
        }
        ClearDialog(*) {
            recording.clear := true
            hook.Stop()
        }
    }
    ApplyPortShortcut(index, chord) {
        before := []
        for row in state.rows
            before.Push(row.shortcut)
        AssignChord(state.rows, index, chord)
        try {
            proposed := ProfilesWithDrafts(availableMonitors, state.drafts, settingsFile)
            ValidateProfileShortcuts(proposed)
            ValidateGlobalShortcuts(proposed, state.globals)
        } catch as err {
            for rowIndex, row in state.rows
                row.shortcut := before[rowIndex]
            ui.status.Value := err.Message
            return false
        }
        for rowIndex, row in state.rows
            ui.rows[rowIndex].button.Text := ShortcutLabel(row.shortcut)
        RefreshActions()
        return true
    }
    ApplyGlobalShortcut(kind, chord) {
        previous := state.globals[kind]
        state.globals[kind] := chord = '' ? '' : ValidateChord(chord)
        try ValidateGlobalShortcuts(ProfilesWithDrafts(availableMonitors, state.drafts, settingsFile), state.globals)
        catch as err {
            state.globals[kind] := previous
            ui.status.Value := err.Message
            return false
        }
        ui.cycleButton.Text := ShortcutLabel(state.globals['cycle'])
        ui.settingsButton.Text := ShortcutLabel(state.globals['settings'])
        ui.downButton.Text := ShortcutLabel(state.globals['brightnessDown'])
        ui.upButton.Text := ShortcutLabel(state.globals['brightnessUp'])
        ui.nextButton.Text := ShortcutLabel(state.globals['brightnessNext'])
        RefreshActions()
        return true
    }
    ToggleLink(*) {
        if availableMonitors.Length < 2
            return
        state.linked := !state.linked
        ui.linkButton.Text := state.linked ? 'Linked' : 'Independent'
        RefreshActions()
    }
    HoverGlobals() {
        if state.capturing
            return
        MouseGetPos(&mx, &my, , &hwnd, 2)
        hovered := ''
        if hwnd = ui.cycleName.Hwnd || hwnd = ui.cycleButton.Hwnd
            hovered := 'cycle'
        else if hwnd = ui.settingsName.Hwnd || hwnd = ui.settingsButton.Hwnd
            hovered := 'settings'
        else if hwnd = ui.downName.Hwnd || hwnd = ui.downButton.Hwnd
            hovered := 'brightnessDown'
        else if hwnd = ui.upName.Hwnd || hwnd = ui.upButton.Hwnd
            hovered := 'brightnessUp'
        else if hwnd = ui.nextName.Hwnd || hwnd = ui.nextButton.Hwnd
            hovered := 'brightnessNext'
        else if hwnd = ui.linkName.Hwnd || hwnd = ui.linkButton.Hwnd
            hovered := 'brightnessLink'
        if hovered = state.hover
            return
        ToolTip(, , , 20)
        if hovered = 'cycle'
            ToolTip('Switches to the next checked input on the selected monitor.', mx + 14, my + 18, 20)
        else if hovered = 'settings'
            ToolTip('Opens Settings for the selected monitor.', mx + 14, my + 18, 20)
        else if hovered = 'brightnessDown'
            ToolTip('Decrease brightness: tap for 1, repeat quickly for 5-point steps, or hold 0.5 seconds for animated 10-point steps.', mx + 14, my + 18, 20)
        else if hovered = 'brightnessUp'
            ToolTip('Increase brightness: tap for 1, repeat quickly for 5-point steps, or hold 0.5 seconds for animated 10-point steps.', mx + 14, my + 18, 20)
        else if hovered = 'brightnessNext'
            ToolTip('Select the next monitor. With the brightness panel open, hold * for 1 second to link or unlink brightness.', mx + 14, my + 18, 20)
        else if hovered = 'brightnessLink'
            ToolTip('Apply the same brightness value to all detected monitors.', mx + 14, my + 18, 20)
        state.hover := hovered
    }
    CheckControl(*) {
        global busy
        if state.loading || state.capturing || busy
            return
        busy := true
        try ui.status.Value := ControlStatus(availableMonitors[state.index]).message
        finally busy := false
    }
    BackupConfig(*) {
        if state.loading || state.capturing
            return
        if ui.save.Visible {
            ui.status.Value := 'Save your changes before creating a backup.'
            return
        }
        path := FileSelect('S', A_MyDocuments '\SwitchMonitor-backup.ini',
            'Back up SwitchMonitor settings', 'INI files (*.ini)')
        if path = ''
            return
        if !RegExMatch(path, 'i)\.ini$')
            path .= '.ini'
        try {
            SaveSettingsBackup(settingsFile, path)
            ui.status.Value := 'Settings backup saved.'
        } catch as err
            ui.status.Value := 'Backup failed: ' err.Message
    }
    RestoreConfig(*) {
        global profileError, brightnessLinked, monitorProfiles
        if state.loading || state.capturing
            return
        if ui.save.Visible {
            ui.status.Value := 'Save or cancel your changes before restoring.'
            return
        }
        path := FileSelect(, A_MyDocuments, 'Restore SwitchMonitor settings',
            'INI files (*.ini)')
        if path = ''
            return
        if !IsSettingsBackup(path) {
            ui.status.Value := 'Select a backup made by SwitchMonitor.'
            return
        }
        if !uiTest && MsgBox('Restore this backup and replace current settings?',
            'Restore settings', 'YesNo Icon!') != 'Yes'
            return
        rollback := AppPath('settings-restore-rollback.ini')
        existed := FileExist(settingsFile) != ''
        originalStartup := StartsWithWindows()
        restored := false
        try {
            if existed
                FileCopy(settingsFile, rollback, 1)
            FileCopy(path, settingsFile, 1)
            LoadGlobalShortcuts()
            RefreshAvailableMonitors()
            if profileError != ''
                throw Error(profileError)
            ValidateGlobalShortcuts(monitorProfiles, globalShortcuts)
            RegisterGlobalShortcuts()
            brightnessLinked := IniRead(settingsFile, 'Brightness', 'Linked', '0') = '1'
            ConfigureStartup(IniRead(path, 'SwitchMonitorBackup', 'StartWithWindows', '0') = '1')
            BrightnessDestroyPanel()
            restored := true
        } catch as err {
            try {
                if existed && FileExist(rollback)
                    FileCopy(rollback, settingsFile, 1)
                else if !existed && FileExist(settingsFile)
                    FileDelete(settingsFile)
                LoadGlobalShortcuts()
                RefreshAvailableMonitors()
                RegisterGlobalShortcuts()
                brightnessLinked := IniRead(settingsFile, 'Brightness', 'Linked', '0') = '1'
                ConfigureStartup(originalStartup)
                ui.status.Value := 'Restore failed: ' err.Message
            } catch as rollbackError
                ui.status.Value := 'Restore failed; recovery also failed: ' rollbackError.Message
        } finally {
            if FileExist(rollback)
                FileDelete(rollback)
        }
        if restored {
            Close()
            OpenLearning()
        }
    }
    Commit(*) {
        global globalShortcuts, brightnessLinked
        if state.loading || state.capturing
            return
        try {
            proposed := ProfilesWithDrafts(availableMonitors, state.drafts, settingsFile)
            ValidateProfileShortcuts(proposed)
            ValidateGlobalShortcuts(proposed, state.globals)
            if StrUpper(state.mainDisplayName) != StrUpper(state.originalMainDisplayName) {
                targetMonitor := 0
                for monitor in availableMonitors
                    if StrUpper(BrightnessDisplayName(monitor)) = StrUpper(state.mainDisplayName)
                        && BrightnessCanBeMainDisplay(monitor) {
                        targetMonitor := monitor
                        break
                    }
                if !IsObject(targetMonitor)
                    throw Error('The selected main display is no longer available in Windows.')
                changed := BrightnessSetMainDisplay(targetMonitor.key)
                if !changed.ok
                    throw Error('Could not change the main display: ' changed.error)
            }
            for monitor in availableMonitors
                if state.drafts.Has(monitor.key)
                    SaveAssignments(settingsFile, monitor, RowsToAssignments(state.drafts[monitor.key]))
            LoadMonitorProfiles()
            for kind, chord in state.globals {
                globalShortcuts[kind] := chord
                IniWrite(chord, settingsFile, 'GlobalShortcuts', kind)
            }
            if state.linked && !brightnessLinked {
                BrightnessFlushSlider()
                if !BrightnessMatchLinkedValues()
                    throw Error('Could not synchronize monitor brightness before linking.')
            }
            brightnessLinked := state.linked
            IniWrite(brightnessLinked ? '1' : '0', settingsFile, 'Brightness', 'Linked')
            BrightnessRefreshPanel()
            SelectActiveMonitor(state.index)
            RegisterShortcuts()
            RegisterGlobalShortcuts()
            Close()
            if !uiTest
                ShowAssignments()
        } catch as err {
            ui.status.Value := err.Message
            if uiTest
                throw err
        }
    }
    Close(*) {
        global wizardOpen
        if state.closing
            return 1
        if state.capturing
            return 1
        if state.loading {
            state.closePending := true
            return 1
        }
        state.closing := true
        SetTimer(HoverGlobals, 0)
        ToolTip(, , , 20)
        wizardOpen := false
        CloseSmooth(window)
        return 1
    }
}

SaveSettingsBackup(source, target) {
    if FileExist(source)
        FileCopy(source, target, 1)
    else
        FileOpen(target, 'w', 'UTF-8').Close()
    IniWrite('1', target, 'SwitchMonitorBackup', 'Version')
    IniWrite(StartsWithWindows() ? '1' : '0', target,
        'SwitchMonitorBackup', 'StartWithWindows')
}

IsSettingsBackup(path) {
    return FileExist(path) && IniRead(path, 'SwitchMonitorBackup', 'Version', '') = '1'
}

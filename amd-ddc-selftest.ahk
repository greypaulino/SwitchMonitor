#Requires AutoHotkey v2.0
#Include amdLgDdc.ahk
try {
    for code, expected in Map(17, '6E508403F40090DD', 18, '6E508403F40091DC', 15, '6E508403F400D09D') {
        packet := AmdLgDdc.Packet(code), actual := ''
        Loop packet.Size
            actual .= Format('{:02X}', NumGet(packet, A_Index - 1, 'UChar'))
        if actual != expected
            throw Error('Paquete LG AMD incorrecto: ' actual)
    }
    rejected := false
    try AmdLgDdc.Packet(16)
    catch
        rejected := true
    if !rejected
        throw Error('AMD acepto un puerto que no existe.')
    display := Buffer(552, 0)
    NumPut('Int', 9, display, 0)
    NumPut('Int', 4, display, 8)
    StrPut('LG HDR WFHD', display.Ptr + 20, 256, 'CP0')
    NumPut('UInt', 3, 'UInt', 3, display, 544)
    record := AmdLgDdc.DisplayRecord(display.Ptr)
    if record.index != 9 || record.adapter != 4 || record.flags != 3 || record.name != 'LG HDR WFHD'
        throw Error('Estructura ADLDisplayInfo incorrecta.')
    target := {adapter: 4, display: 9, name: record.name}
    chosen := AmdLgDdc.SelectTarget([target], 'LG HDR WFHD')
    if chosen.adapter != 4 || chosen.display != 9
        throw Error('Se uso un indice fijo para la pantalla AMD.')
    for candidates in [[], [target, target], [{adapter: 4, display: 9, name: 'Otro monitor'}]] {
        rejected := false
        try AmdLgDdc.SelectTarget(candidates, 'LG HDR WFHD')
        catch
            rejected := true
        if !rejected
            throw Error('AMD acepto una pantalla ambigua o distinta.')
    }
    FileOpen(AppPath('amd-ddc-selftest.txt'), 'w', 'UTF-8').Write('PASS: paquetes LG, checksum, ABI ADLDisplayInfo, indices dinamicos y rechazo de destinos ambiguos. Sin pruebas fisicas AMD.`n')
} catch as err {
    FileOpen(AppPath('amd-ddc-selftest.txt'), 'w', 'UTF-8').Write('FAIL: ' err.Message '`n')
    ExitApp(1)
}
ExitApp(0)

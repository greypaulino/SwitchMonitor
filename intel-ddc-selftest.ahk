#Requires AutoHotkey v2.0
#SingleInstance Off
#Include intelLegacyDdc.ahk
#Include appPaths.ahk
try {
    if IntelLegacyDdc.LgValue(17) != 0x90 || IntelLegacyDdc.LgValue(18) != 0x91 || IntelLegacyDdc.LgValue(15) != 0xD0
        throw Error('Mapeo de entradas LG incorrecto.')
    rejected := false
    try IntelLegacyDdc.LgValue(16)
    catch
        rejected := true
    if !rejected
        throw Error('Se acepto una entrada inexistente para el perfil LG.')
    request := IntelLegacyDdc.BuildRequest(256, 0x50, [0x84, 3, 0xF4, 0, 0xD0])
    if request.Size != 184 || NumGet(request, 24, 'UInt') != 256 || NumGet(request, 32, 'UInt') != 0x6E
        || NumGet(request, 36, 'UInt') != 0x50 || NumGet(request, 40, 'UInt') != 3
        || NumGet(request, 44, 'UInt') != 5 || NumGet(request, 48, 'UInt') != 0
        || NumGet(request, 54, 'UChar') != 0xF4 || NumGet(request, 56, 'UChar') != 0xD0
        throw Error('Formato de paquete Intel incorrecto.')
    response := Buffer(184, 0)
    ; Real read-only response captured from this monitor (brightness=30).
    for index, value in [0x6E, 0x88, 2, 0, 0x10, 0, 0, 100, 0, 30, 0xDE]
        NumPut('UChar', value, response, 51 + index)
    if IntelLegacyDdc.ParseVcpReply(response, 0x10) != 30
        throw Error('Lectura del valor VCP incorrecta.')
    NumPut('UChar', 31, response, 61)
    rejected := false
    try IntelLegacyDdc.ParseVcpReply(response, 0x10)
    catch
        rejected := true
    if !rejected
        throw Error('Se acepto una respuesta con checksum invalido.')
    FileOpen(AppPath('intel-ddc-selftest.txt'), 'w', 'UTF-8').Write('PASS: paquete Intel, direcciones, entradas LG y checksum de respuesta. Sin escrituras al monitor.`n')
} catch as err {
    FileOpen(AppPath('intel-ddc-selftest.txt'), 'w', 'UTF-8').Write('FAIL: ' err.Message '`n')
    ExitApp(1)
}
ExitApp(0)

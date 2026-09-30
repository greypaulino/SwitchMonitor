; Intel legacy CUI transport for the installed HD Graphics 3000 driver.
; ABI verified from the local type libraries and igfxsrvc.exe 8.15.10.4459.
; No external DLL or driver installation is needed.
#Include appPaths.ahk
class IntelLegacyDdc {
    __New(monitorDevice, logOperations := true) {
        this.logOperations := logOperations
        if A_PtrSize != 8
            throw Error('La ruta Intel antigua requiere AutoHotkey de 64 bits.')
        version := FileGetVersion(A_WinDir '\System32\igfxsrvc.exe')
        if version != '8.15.10.4459'
            throw Error('Formato Intel no validado para el controlador ' version '. No se envio ningun cambio.')
        if !RegExMatch(monitorDevice, 'i)^(\\\\\.\\DISPLAY\d+)\\Monitor\d+$', &match)
            throw Error('Identificador de pantalla Windows no reconocido: ' monitorDevice)
        this.device := match[1]
        this.driver := ComObject('{9CEE304E-DC6C-11D2-B561-00A0C92E6848}', '{BB74AF4F-DC70-11D2-B561-00A0C92E6848}')
        this.guid := Buffer(16, 0)
        DllCall('ole32\CLSIDFromString', 'Str', '{1B9FD916-DA16-4A51-AFBE-8FA712FBB5CE}', 'Ptr', this.guid)
        api := ComObject('{7160A13D-73DA-4CEA-95B9-37356478588A}', '{27E7234F-429F-4787-AC8F-8AADDED01355}')
        name := DllCall('oleaut32\SysAllocString', 'Str', this.device, 'Ptr')
        found := []
        try {
            Loop 16 {
                uid := 0, kind := 0
                hr := ComCall(3, api, 'Ptr', name, 'UInt', A_Index - 1, 'UInt*', &uid, 'UInt*', &kind, 'Int')
                if hr != 0
                    break
                if uid
                    found.Push(uid)
            }
        } finally {
            DllCall('oleaut32\SysFreeString', 'Ptr', name)
        }
        if found.Length != 1
            throw Error('Intel devuelve ' found.Length ' pantallas activas para ' this.device '. No se puede elegir el monitor sin ambiguedad.')
        this.uid := found[1]
    }

    static LgValue(code) {
        values := Map(17, 0x90, 18, 0x91, 15, 0xD0)
        if !values.Has(code)
            throw Error('Entrada no documentada para el perfil LG 29WK600: ' code)
        return values[code]
    }

    static BuildRequest(uid, source, payload, receiveBytes := 0) {
        if payload.Length > 128 || receiveBytes < 0 || receiveBytes > 128
            throw Error('Paquete I2C fuera de los limites.')
        ; Native 184-byte Intel request, no embedded pointers. The driver
        ; prepends address/subaddress and appends the DDC checksum (flags=3).
        data := Buffer(184, 0)
        NumPut('UInt', uid, 'UInt', 0, 'UInt', 0x6E, 'UInt', source,
            'UInt', 3, 'UInt', payload.Length, 'UInt', receiveBytes, data, 24)
        for index, value in payload
            NumPut('UChar', value, data, 51 + index)
        return data
    }

    GetVcp(feature) {
        data := IntelLegacyDdc.BuildRequest(this.uid, 0x51, [0x82, 0x01, feature], 11)
        this.Exchange(data, false)
        return IntelLegacyDdc.ParseVcpReply(data, feature)
    }

    static ParseVcpReply(data, feature) {
        checksum := 0x50
        Loop 11
            checksum ^= NumGet(data, 51 + A_Index, 'UChar')
        if checksum != 0 || NumGet(data, 52, 'UChar') != 0x6E
            || NumGet(data, 53, 'UChar') != 0x88 || NumGet(data, 54, 'UChar') != 2
            || NumGet(data, 55, 'UChar') != 0 || NumGet(data, 56, 'UChar') != feature
            throw Error('Respuesta DDC invalida o funcion no admitida: ' Format('{:02X}', feature))
        return (NumGet(data, 60, 'UChar') << 8) | NumGet(data, 61, 'UChar')
    }

    SendLgInput(code) {
        value := IntelLegacyDdc.LgValue(code)
        data := IntelLegacyDdc.BuildRequest(this.uid, 0x50, [0x84, 0x03, 0xF4, 0, value])
        this.Exchange(data, true)
    }

    Exchange(data, write) {
        hr := ComCall(write ? 4 : 3, this.driver, 'Ptr', this.guid, 'Int', data.Size, 'Ptr', data, 'Int')
        status := NumGet(data, 0, 'UChar')
        if this.logOperations
            FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' intel-cui device=' this.device
                ' uid=' this.uid ' write=' write ' source=' Format('{:02X}', NumGet(data, 36, 'UInt'))
                ' HRESULT=' Format('{:08X}', hr & 0xFFFFFFFF) ' status=' status '`n',
                AppPath('monitor-switch.log'), 'UTF-8')
        if hr != 0 || status != 0
            throw Error(Format('Intel CUI rechazo la operacion: HRESULT={:08X}, estado={}.', hr & 0xFFFFFFFF, status))
    }
}

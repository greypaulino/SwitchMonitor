#Include appPaths.ahk
; AMD ADL2, Windows x64 ABI from AMD's public display-library headers.
; This backend needs physical validation on the AMD computer.
class AmdLgDdc {
    __New(monitorDevice, monitorName) {
        this.module := 0, this.context := 0, this.allocator := 0
        if A_PtrSize != 8
            throw Error('AMD ADL requiere la version de 64 bits.')
        if !RegExMatch(monitorDevice, 'i)^(\\\\\.\\DISPLAY\d+)\\Monitor\d+$', &match)
            throw Error('Identificador de pantalla no reconocido: ' monitorDevice)
        this.device := match[1]
        library := A_WinDir '\System32\atiadlxx.dll'
        this.module := DllCall('LoadLibraryExW', 'Str', library, 'Ptr', 0, 'UInt', 0x800, 'Ptr')
        if !this.module
            throw Error('El controlador AMD instalado no proporciona atiadlxx.dll. No descargues esa DLL por separado.')
        this.allocator := CallbackCreate((bytes) => DllCall('msvcrt\malloc', 'UPtr', bytes, 'Ptr'), 'C', 1)
        context := 0
        rc := DllCall(this.Proc('ADL2_Main_Control_Create'), 'Ptr', this.allocator, 'Int', 1, 'Ptr*', &context, 'Cdecl Int')
        this.context := context
        this.Check(rc, 'iniciar ADL2')
        this.writeProc := this.Proc('ADL2_Display_DDCBlockAccess_Get')
        count := 0
        this.Check(DllCall(this.Proc('ADL2_Adapter_NumberOfAdapters_Get'), 'Ptr', this.context, 'Int*', &count, 'Cdecl Int'), 'enumerar adaptadores')
        if count < 1 || count > 128
            throw Error('AMD no devolvio una lista valida de adaptadores.')
        adapters := Buffer(count * 1572, 0)
        Loop count
            NumPut('Int', 1572, adapters, (A_Index - 1) * 1572)
        this.Check(DllCall(this.Proc('ADL2_Adapter_AdapterInfo_Get'), 'Ptr', this.context, 'Ptr', adapters, 'Int', adapters.Size, 'Cdecl Int'), 'leer adaptadores')
        candidates := []
        Loop count {
            offset := (A_Index - 1) * 1572
            adapter := NumGet(adapters, offset + 4, 'Int')
            device := StrGet(adapters.Ptr + offset + 536, 256, 'CP0')
            FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' amd-adapter index=' adapter
                ' device=' device ' present=' NumGet(adapters, offset + 792, 'Int')
                ' requested-device=' this.device '`n', AppPath('monitor-switch.log'), 'UTF-8')
            if StrLower(device) != StrLower(this.device) || !NumGet(adapters, offset + 792, 'Int')
                continue
            displays := 0, displayCount := 0
            try {
                this.Check(DllCall(this.Proc('ADL2_Display_DisplayInfo_Get'), 'Ptr', this.context,
                    'Int', adapter, 'Int*', &displayCount, 'Ptr*', &displays, 'Int', 0, 'Cdecl Int'), 'enumerar pantallas')
                if displayCount < 0 || displayCount > 128 || (displayCount && !displays)
                    throw Error('Lista de pantallas AMD invalida.')
                Loop displayCount {
                    item := displays + (A_Index - 1) * 552
                    info := AmdLgDdc.DisplayRecord(item)
                    FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' amd-enumerate device=' device
                        ' adapter=' adapter ' display=' info.index ' mapped-adapter=' info.adapter
                        ' flags=' info.flags ' name=' info.name '`n', AppPath('monitor-switch.log'), 'UTF-8')
                    if info.adapter = adapter && (info.flags & 3) = 3
                        candidates.Push({adapter: adapter, display: info.index, name: info.name})
                }
            } finally {
                if displays
                    DllCall('msvcrt\free', 'Ptr', displays)
            }
        }
        target := AmdLgDdc.SelectTarget(candidates, monitorName)
        this.adapter := target.adapter, this.display := target.display
    }

    static DisplayRecord(pointer) {
        return {index: NumGet(pointer, 0, 'Int'), adapter: NumGet(pointer, 8, 'Int'),
            name: StrGet(pointer + 20, 256, 'CP0'),
            flags: NumGet(pointer, 544, 'UInt') & NumGet(pointer, 548, 'UInt')}
    }

    static SelectTarget(candidates, monitorName) {
        ; Never guess a display index or send to every output in a clone group.
        if candidates.Length != 1
            throw Error('AMD: se encontraron ' candidates.Length ' pantallas conectadas y asignadas a esta salida. Seleccion no univoca; no se enviaron comandos.')
        candidate := candidates[1]
        if StrLower(Trim(candidate.name)) != StrLower(Trim(monitorName))
            throw Error('AMD: el nombre de pantalla no coincide (' candidate.name ' / ' monitorName '). No se enviaron comandos.')
        return candidate
    }

    static Packet(code) {
        values := Map(17, 0x90, 18, 0x91, 15, 0xD0)
        if !values.Has(code)
            throw Error('Entrada LG no admitida: ' code)
        return AmdLgDdc.PacketFor(0x50, [0x84, 3, 0xF4, 0, values[code]])
    }

    static PacketFor(source, payload) {
        bytes := [0x6E, source]
        for value in payload
            bytes.Push(value)
        result := Buffer(bytes.Length + 1, 0), checksum := 0
        for index, value in bytes {
            NumPut('UChar', value, result, index - 1)
            checksum ^= value
        }
        NumPut('UChar', checksum, result, bytes.Length)
        return result
    }

    ReadInput() {
        request := AmdLgDdc.PacketFor(0x51, [0x82, 1, 0x60])
        received := 0
        this.Check(DllCall(this.writeProc, 'Ptr', this.context, 'Int', this.adapter,
            'Int', this.display, 'Int', 0, 'Int', 0, 'Int', request.Size,
            'Ptr', request, 'Int*', &received, 'Ptr', 0, 'Cdecl Int'),
            'consultar la entrada LG')
        Sleep(50)
        readCommand := Buffer(1, 0), reply := Buffer(16, 0)
        NumPut('UChar', 0x6F, readCommand)
        received := reply.Size
        this.Check(DllCall(this.writeProc, 'Ptr', this.context, 'Int', this.adapter,
            'Int', this.display, 'Int', 0, 'Int', 0, 'Int', readCommand.Size,
            'Ptr', readCommand, 'Int*', &received, 'Ptr', reply, 'Cdecl Int'),
            'leer la entrada LG')
        value := AmdLgDdc.ParseInputReply(reply, received)
        FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' amd-adl2 input=' value
            ' device=' this.device ' adapter=' this.adapter ' display=' this.display '`n',
            AppPath('monitor-switch.log'), 'UTF-8')
        return value
    }

    static ParseInputReply(reply, received) {
        checksum := 0x50
        if received < 11 || NumGet(reply, 0, 'UChar') != 0x6E
            || NumGet(reply, 1, 'UChar') != 0x88
            || NumGet(reply, 2, 'UChar') != 2
            || NumGet(reply, 3, 'UChar') != 0
            || NumGet(reply, 4, 'UChar') != 0x60
            throw Error('AMD devolvio una respuesta de entrada LG invalida.')
        Loop 11
            checksum ^= NumGet(reply, A_Index - 1, 'UChar')
        if checksum != 0
            throw Error('AMD devolvio una respuesta LG con checksum invalido.')
        value := (NumGet(reply, 8, 'UChar') << 8)
            | NumGet(reply, 9, 'UChar')
        return (value = 17 || value = 18 || value = 15) ? value : 0
    }

    SendLgInput(code) {
        packet := AmdLgDdc.Packet(code)
        this.SendPacket(packet, code, 'LG F4')
    }

    SendStandardInput(code) {
        if code != 17 && code != 18 && code != 15
            throw Error('Entrada LG no admitida: ' code)
        packet := AmdLgDdc.PacketFor(0x51, [0x84, 3, 0x60, 0, code])
        this.SendPacket(packet, code, 'VCP 60')
    }

    SendPacket(packet, code, method) {
        received := 0
        rc := DllCall(this.writeProc, 'Ptr', this.context, 'Int', this.adapter, 'Int', this.display,
            'Int', 0, 'Int', 0, 'Int', packet.Size, 'Ptr', packet,
            'Int*', &received, 'Ptr', 0, 'Cdecl Int')
        FileAppend(FormatTime(, 'yyyy-MM-dd HH:mm:ss') ' amd-adl2 device=' this.device
            ' adapter=' this.adapter ' display=' this.display ' method=' method
            ' requested=' code ' result=' rc
            ' physical=unverified`n', AppPath('monitor-switch.log'), 'UTF-8')
        this.Check(rc, 'enviar entrada LG')
    }

    Proc(name) {
        address := DllCall('GetProcAddress', 'Ptr', this.module, 'AStr', name, 'Ptr')
        if !address
            throw Error('El controlador AMD no proporciona ' name '.')
        return address
    }

    Check(result, operation) {
        if result != 0
            throw Error('AMD ADL2 no pudo ' operation '. Codigo: ' result '.')
    }

    __Delete() {
        if this.context {
            try DllCall(this.Proc('ADL2_Main_Control_Destroy'), 'Ptr', this.context, 'Cdecl Int')
        }
        if this.allocator
            CallbackFree(this.allocator)
        if this.module
            DllCall('FreeLibrary', 'Ptr', this.module)
    }
}

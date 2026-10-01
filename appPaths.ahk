; Installed builds keep user data outside the application directory.
; portable.flag opts into a self-contained data folder beside the executable.
; installed.flag also covers releases bundled with the AutoHotkey interpreter.
AppPath(name := '') {
    static directory := FileExist(A_ScriptDir '\portable.flag')
        ? A_ScriptDir '\data'
        : (A_IsCompiled || FileExist(A_ScriptDir '\installed.flag')
            ? EnvGet('LOCALAPPDATA') '\SwitchMonitor' : A_ScriptDir '\data')
    DirCreate(directory)
    return directory (name = '' ? '' : '\' name)
}

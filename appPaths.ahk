; Installed builds keep user data outside the application directory.
; portable.flag opts into a self-contained data folder beside the executable.
AppPath(name := '') {
    static directory := A_IsCompiled
        ? (FileExist(A_ScriptDir '\portable.flag') ? A_ScriptDir '\data' : EnvGet('LOCALAPPDATA') '\SwitchMonitor')
        : A_ScriptDir '\data'
    DirCreate(directory)
    return directory (name = '' ? '' : '\' name)
}

using System.Runtime.CompilerServices;
using SwitchMonitor.Core.Diagnostics;

namespace SwitchMonitor.App.Services;

/// <summary>Lines in the log about what moves the selected display and what makes a display unavailable (to find out why a choice is lost).</summary>
internal static class Trace
{
    public static ICoreLog? Log { get; set; }

    public static void Write(string text, [CallerMemberName] string caller = "") => Log?.Write(LogLevel.Info, $"[{caller}] {text}");
}

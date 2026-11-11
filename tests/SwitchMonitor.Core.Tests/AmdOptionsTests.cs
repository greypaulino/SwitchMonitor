using SwitchMonitor.Core;

namespace SwitchMonitor.Core.Tests;

/// <summary>AMD writes are on by default (validated on a Radeon 780M with the LG), with a file to switch them off.</summary>
internal static class AmdOptionsTests
{
    [Test]
    public static void AmdWritesAreOnUnlessTheOffFileIsThere()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"smamd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            Assert.True(CoreOptions.FromDataDirectory(folder).AllowAmdWrites, "on by default");
            File.WriteAllText(Path.Combine(folder, "amd-experimental.flag"), "");
            Assert.True(CoreOptions.FromDataDirectory(folder).AllowAmdWrites, "the old flag changes nothing");
            File.WriteAllText(Path.Combine(folder, "amd-writes-off.flag"), "");
            Assert.True(!CoreOptions.FromDataDirectory(folder).AllowAmdWrites, "the off file switches them off");
        }
        finally { Directory.Delete(folder, true); }
    }
}

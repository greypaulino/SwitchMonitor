using System.Globalization;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Capabilities;

/// <summary>Parsed MCCS capabilities string (the reply to a DDC/CI Capabilities Request).</summary>
public sealed class MonitorCapabilities
{
    private readonly Dictionary<byte, IReadOnlyList<int>> vcp;

    internal MonitorCapabilities(string raw, IReadOnlyDictionary<string, string> sections,
        Dictionary<byte, IReadOnlyList<int>> vcp)
    {
        Raw = raw;
        Sections = sections;
        this.vcp = vcp;
    }

    public static MonitorCapabilities Empty { get; } =
        new("", new Dictionary<string, string>(), new Dictionary<byte, IReadOnlyList<int>>());

    public string Raw { get; }

    /// <summary>Top-level sections by lower-case key: prot, type, model, cmds, vcp, mccs_ver ...</summary>
    public IReadOnlyDictionary<string, string> Sections { get; }

    public string? Model => Sections.TryGetValue("model", out var value) ? value : null;
    public string? Type => Sections.TryGetValue("type", out var value) ? value : null;
    public string? MccsVersion => Sections.TryGetValue("mccs_ver", out var value) ? value : null;

    /// <summary>Every advertised VCP code, in ascending order.</summary>
    public IReadOnlyList<byte> VcpCodes => vcp.Keys.Order().ToArray();

    public bool Supports(byte code) => vcp.ContainsKey(code);

    /// <summary>
    /// Values listed for a non-continuous feature, e.g. 60(11 12 0F) → [0x11, 0x12, 0x0F].
    /// Empty for continuous features or when the code is not advertised.
    /// </summary>
    public IReadOnlyList<int> ValuesFor(byte code) => vcp.TryGetValue(code, out var values) ? values : [];

    /// <summary>Advertised input sources (VCP 0x60) in a stable display order.</summary>
    public IReadOnlyList<int> InputSources => InputSource.Order(ValuesFor(VcpCode.InputSource));

    public override string ToString() =>
        $"model={Model ?? "?"} vcp=[{string.Join(' ', VcpCodes.Select(c => c.ToString("X2", CultureInfo.InvariantCulture)))}]";
}

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Quirks;

/// <summary>Per-model behavior that differs from the MCCS standard.</summary>
public sealed class MonitorQuirk
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<string> PnpIds { get; init; }

    /// <summary>Physical inputs, in display order, when the capabilities string cannot be trusted.</summary>
    public IReadOnlyList<(int Code, string Name)> Inputs { get; init; } = [];

    /// <summary>Vendor VCP code used for input writes instead of 0x60, if any.</summary>
    public byte? InputWriteVcp { get; init; }

    /// <summary>Standard 0x60 input code → value written to <see cref="InputWriteVcp"/>.</summary>
    public IReadOnlyDictionary<int, uint> InputWriteValues { get; init; } = new Dictionary<int, uint>();

    /// <summary>GPU-specific transports to prefer, in order, before standard DDC/CI.</summary>
    public IReadOnlyList<string> Transports { get; init; } = [];

    public string? Notes { get; init; }

    /// <summary>Vendor controls this model is known to have (e.g. LG picture mode on VCP 0x15).</summary>
    public IReadOnlyList<FeatureDefinition> Controls { get; init; } = [];

    /// <summary>The VCP code and raw value to write to select a standard input code.</summary>
    public (byte Vcp, uint Value) MapInputWrite(int inputCode)
    {
        if (InputWriteVcp is not byte vcp) return (VcpCode.InputSource, (uint)inputCode);
        if (!InputWriteValues.TryGetValue(inputCode, out uint value))
            throw new ArgumentOutOfRangeException(nameof(inputCode), $"{Name} has no input {InputSource.Describe(inputCode)}.");
        return (vcp, value);
    }
}

/// <summary>Loads monitor quirks from the embedded quirks.json (or a supplied JSON document).</summary>
public sealed partial class QuirkDatabase
{
    private readonly List<MonitorQuirk> quirks;

    private QuirkDatabase(List<MonitorQuirk> quirks) => this.quirks = quirks;

    public IReadOnlyList<MonitorQuirk> All => quirks;

    /// <summary>quirks.user.json in the data directory: mappings learned on this computer.</summary>
    public static string UserFilePath => Path.Combine(AppPaths.DataDirectory, "quirks.user.json");

    /// <summary>Embedded quirks plus this computer's learned mappings (user controls win per VCP code).</summary>
    public static QuirkDatabase LoadDefault(string? userFile = null)
    {
        var embedded = LoadEmbedded();
        userFile ??= UserFilePath;
        if (!File.Exists(userFile)) return embedded;
        return embedded.Merge(Parse(File.ReadAllText(userFile)));
    }

    /// <summary>Overlays <paramref name="overlay"/>: same id merges controls by code; new ids are added.</summary>
    public QuirkDatabase Merge(QuirkDatabase overlay)
    {
        var result = new List<MonitorQuirk>(quirks);
        foreach (var extra in overlay.quirks)
        {
            int index = result.FindIndex(q => string.Equals(q.Id, extra.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) { result.Add(extra); continue; }
            var baseQuirk = result[index];
            var controls = baseQuirk.Controls.Where(c => extra.Controls.All(e => e.Code != c.Code)).Concat(extra.Controls)
                .OrderBy(c => c.Code).ToArray();
            result[index] = new MonitorQuirk
            {
                Id = baseQuirk.Id,
                Name = baseQuirk.Name,
                PnpIds = baseQuirk.PnpIds.Union(extra.PnpIds, StringComparer.OrdinalIgnoreCase).ToArray(),
                Inputs = baseQuirk.Inputs,
                InputWriteVcp = baseQuirk.InputWriteVcp,
                InputWriteValues = baseQuirk.InputWriteValues,
                Transports = baseQuirk.Transports,
                Notes = baseQuirk.Notes,
                Controls = controls
            };
        }
        return new QuirkDatabase(result);
    }

    public static QuirkDatabase LoadEmbedded()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SwitchMonitor.Core.quirks.json")
            ?? throw new InvalidOperationException("The embedded quirks.json resource is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static QuirkDatabase Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var result = new List<MonitorQuirk>();
        if (!document.RootElement.TryGetProperty("monitors", out var monitors) || monitors.ValueKind != JsonValueKind.Array)
            throw new FormatException("quirks.json must contain a \"monitors\" array.");
        foreach (var entry in monitors.EnumerateArray())
            result.Add(ParseQuirk(entry));
        var duplicate = result.SelectMany(q => q.PnpIds).GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new FormatException($"PnP ID {duplicate.Key} appears in more than one quirk.");
        return new QuirkDatabase(result);
    }

    /// <summary>Finds the quirk whose PnP ID (e.g. GSM7714) appears in a Windows monitor device ID.</summary>
    public MonitorQuirk? Match(string? deviceId)
    {
        string? pnp = ExtractPnpId(deviceId);
        if (pnp is null) return null;
        return quirks.FirstOrDefault(q => q.PnpIds.Contains(pnp, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>"MONITOR\GSM7714\{...}\0001" → "GSM7714". Returns null when no PnP ID is present.</summary>
    public static string? ExtractPnpId(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        var match = PnpPattern().Match(deviceId);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>EDID product code from a PnP ID: "GSM7715" → 0x7715.</summary>
    public static int? ProductCode(string? pnpId) =>
        pnpId is { Length: 7 } && int.TryParse(pnpId.AsSpan(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)
            ? code : null;

    [GeneratedRegex(@"(?i)(?:^|\\)([A-Z]{3}[0-9A-F]{4})(?:\\|$)")]
    private static partial Regex PnpPattern();

    private static MonitorQuirk ParseQuirk(JsonElement entry)
    {
        string id = RequiredString(entry, "id");
        var inputs = new List<(int, string)>();
        if (entry.TryGetProperty("inputs", out var inputArray))
            foreach (var input in inputArray.EnumerateArray())
                inputs.Add((Hex(RequiredString(input, "code"), id), RequiredString(input, "name")));

        byte? writeVcp = null;
        var writeValues = new Dictionary<int, uint>();
        if (entry.TryGetProperty("inputWrite", out var write))
        {
            writeVcp = (byte)Hex(RequiredString(write, "vcp"), id);
            if (write.TryGetProperty("values", out var values))
                foreach (var pair in values.EnumerateObject())
                    writeValues[Hex(pair.Name, id)] = (uint)Hex(pair.Value.GetString() ?? "", id);
            var unmapped = inputs.Where(input => !writeValues.ContainsKey(input.Item1)).ToArray();
            if (unmapped.Length > 0)
                throw new FormatException($"Quirk {id} lists inputs without an inputWrite value.");
        }

        return new MonitorQuirk
        {
            Id = id,
            Name = RequiredString(entry, "name"),
            PnpIds = entry.GetProperty("pnpIds").EnumerateArray().Select(e => (e.GetString() ?? "").ToUpperInvariant()).ToArray(),
            Inputs = inputs,
            InputWriteVcp = writeVcp,
            InputWriteValues = writeValues,
            Transports = entry.TryGetProperty("transports", out var transports)
                ? transports.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                : [],
            Notes = entry.TryGetProperty("notes", out var notes) ? notes.GetString() : null,
            Controls = entry.TryGetProperty("controls", out var controls)
                ? controls.EnumerateArray().Select(c => ParseControl(c, id)).ToArray()
                : []
        };
    }

    /// <summary>{ "vcp": "15", "name": "Picture mode", "kind": "choice", "values": { "01": "Reader" } }</summary>
    private static FeatureDefinition ParseControl(JsonElement control, string quirkId)
    {
        byte code = (byte)Hex(RequiredString(control, "vcp"), quirkId);
        if (FeatureCatalog.IsCommand(code) || code == VcpCode.InputSource || code == VcpCode.PowerMode)
            throw new FormatException($"Quirk {quirkId}: VCP {code:X2} cannot be a control.");
        var names = new Dictionary<uint, string>();
        if (control.TryGetProperty("values", out var values))
            foreach (var pair in values.EnumerateObject())
                names[(uint)Hex(pair.Name, quirkId)] = pair.Value.GetString() ?? pair.Name;
        string kindText = control.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : (names.Count > 0 ? "choice" : "continuous");
        var kind = kindText.ToLowerInvariant() switch
        {
            "continuous" => FeatureKind.Continuous,
            "choice" => FeatureKind.Choice,
            "toggle" => FeatureKind.Toggle,
            _ => throw new FormatException($"Quirk {quirkId}: kind \"{kindText}\" is not continuous, choice or toggle.")
        };
        string capsFormat = control.TryGetProperty("capabilityValues", out var format) ? format.GetString() ?? "" : "";
        bool decimalCaps = string.Equals(capsFormat, "decimal", StringComparison.OrdinalIgnoreCase);
        bool ignoreCaps = string.Equals(capsFormat, "ignore", StringComparison.OrdinalIgnoreCase);
        if (capsFormat.Length > 0 && !decimalCaps && !ignoreCaps)
            throw new FormatException($"Quirk {quirkId}: capabilityValues must be \"decimal\" or \"ignore\".");
        if (ignoreCaps && names.Count == 0)
            throw new FormatException($"Quirk {quirkId}: capabilityValues \"ignore\" needs named values.");
        uint? maximum = control.TryGetProperty("maximum", out var max) && max.TryGetUInt32(out uint m) && m > 0 ? m : null;
        if (maximum is not null && kind != FeatureKind.Continuous)
            throw new FormatException($"Quirk {quirkId}: only continuous controls take a maximum.");
        bool readOnly = control.TryGetProperty("readOnly", out var ro) && ro.ValueKind == JsonValueKind.True;
        return new FeatureDefinition(code, RequiredString(control, "name"), kind, readOnly ? FeatureRisk.ReadOnly : FeatureRisk.Safe, names)
        {
            CapabilityValuesAreDecimal = decimalCaps,
            IgnoreCapabilityValues = ignoreCaps,
            Maximum = maximum
        };
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new FormatException($"quirks.json: missing \"{name}\".");

    private static int Hex(string text, string quirkId) =>
        int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) && value is >= 0 and <= 0xFFFF
            ? value
            : throw new FormatException($"quirks.json ({quirkId}): \"{text}\" is not a hex value.");
}

namespace SwitchMonitor.Core;

/// <summary>
/// The short names the interface shows: type, brand and model only ("Monitor LG 29WK600", "Laptop DELL N5050").
/// The device's own <see cref="MonitorDevice.Name"/> stays as it was, since profiles saved "Only for" a display match it.
/// </summary>
public static class DisplayNames
{
    /// <summary>EDID manufacturer codes (first three letters of the PnP ID) to brand names.</summary>
    private static readonly Dictionary<string, string> Brands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GSM"] = "LG", ["LGD"] = "LG", ["DEL"] = "Dell", ["SAM"] = "Samsung", ["SEC"] = "Samsung", ["ACI"] = "ASUS",
        ["AUS"] = "ASUS", ["ACR"] = "Acer", ["BNQ"] = "BenQ", ["AOC"] = "AOC", ["HWP"] = "HP", ["HPN"] = "HP",
        ["LEN"] = "Lenovo", ["PHL"] = "Philips", ["VSC"] = "ViewSonic", ["MSI"] = "MSI", ["GBT"] = "Gigabyte",
        ["ENC"] = "EIZO", ["NEC"] = "NEC", ["SNY"] = "Sony", ["IVM"] = "iiyama", ["HKC"] = "HKC", ["MZI"] = "Xiaomi",
        ["XMI"] = "Xiaomi", ["APP"] = "Apple", ["SHP"] = "Sharp", ["TSB"] = "Toshiba", ["FUS"] = "Fujitsu", ["KOS"] = "KOORUI",
        ["CMO"] = "Chimei", ["AUO"] = "AU Optronics", ["BOE"] = "BOE"
    };

    public static string? Brand(string? pnpId) =>
        pnpId is { Length: >= 3 } && Brands.TryGetValue(pnpId[..3], out var brand) ? brand : null;

    /// <summary>"Monitor LG 29WK600", "Monitor Dell U2723QE", "Laptop DELL N5050".</summary>
    public static string For(MonitorDevice device)
    {
        if (device.Identity.IsBuiltIn) return device.Identity.Label ?? "Laptop";
        if (device.Quirk?.Name is { Length: > 0 } known) return "Monitor " + known;
        string? brand = Brand(device.Identity.PnpId);
        string? model = device.CachedModel;
        // The name the monitor reports in its EDID comes first ("Odyssey G5"): the capabilities model can be an internal
        // codename (the same monitor answers "LEMON23") and Windows often says "Generic PnP Monitor".
        string rest = device.Identity.Edid?.Name is { Length: > 0 } edidName ? edidName
            : model is { Length: > 0 } ? model : device.Identity.FriendlyName.Trim();
        if (rest.Length == 0 || rest.StartsWith("Generic", StringComparison.OrdinalIgnoreCase)) rest = "";
        // A long marketing name keeps its first words only (brand and model come first in EDID names).
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3).ToList();
        if (brand is not null && words.Count > 0 && string.Equals(words[0], brand, StringComparison.OrdinalIgnoreCase)) words.RemoveAt(0);
        // A series name followed by a short model ("Odyssey G5") keeps both; a plain model code keeps one.
        int keep = words.Count >= 2 && !words[0].Any(char.IsDigit) && words[1].Length <= 4 && words[1].Any(char.IsDigit) ? 2 : 1;
        string name = string.Join(' ', new[] { brand }.Concat(words.Take(keep)).Where(w => !string.IsNullOrEmpty(w)));
        return name.Length == 0 ? "Monitor" : "Monitor " + name;
    }

    /// <summary>"Dell Inc." + "Inspiron N5050" → "Laptop DELL N5050"; unknown parts are left out.</summary>
    public static string Laptop(string? maker, string? model)
    {
        string brand = LaptopBrand(maker);
        string m = (model ?? "").Trim();
        if (m.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(m,
                @"(?i)^(?:N/?A|Unknown|Default string|System Product Name|To Be Filled By O\.E\.M\.)$")) m = "";
        var shortName = System.Text.RegularExpressions.Regex.Match(m, @"(?i)\b([A-Z]+\d{3,}[A-Z]?)$");
        if (shortName.Success) m = shortName.Groups[1].Value;
        if (brand.Length > 0 && m.StartsWith(brand, StringComparison.OrdinalIgnoreCase)) m = m[brand.Length..].Trim();
        return string.Join(' ', new[] { "Laptop", brand, m }.Where(p => p.Length > 0));
    }

    private static string LaptopBrand(string? maker)
    {
        string text = (maker ?? "").Trim();
        if (text.Length == 0 || text.Contains("O.E.M", StringComparison.OrdinalIgnoreCase) || text.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase))
            return "";
        string lower = text.ToLowerInvariant();
        return lower switch
        {
            _ when lower.StartsWith("dell") => "DELL",
            _ when lower.StartsWith("hewlett") || lower == "hp" || lower.StartsWith("hp ") => "HP",
            _ when lower.StartsWith("lenovo") => "Lenovo",
            _ when lower.StartsWith("asus") => "ASUS",
            _ when lower.StartsWith("acer") => "Acer",
            _ when lower.StartsWith("micro-star") || lower.StartsWith("msi") => "MSI",
            _ when lower.StartsWith("microsoft") => "Microsoft",
            _ when lower.StartsWith("samsung") => "Samsung",
            _ when lower.StartsWith("toshiba") => "Toshiba",
            _ when lower.StartsWith("lg") => "LG",
            _ when lower.StartsWith("apple") => "Apple",
            _ when lower.StartsWith("huawei") => "Huawei",
            _ when lower.StartsWith("sony") => "Sony",
            _ => text.Split(' ', ',')[0]
        };
    }
}

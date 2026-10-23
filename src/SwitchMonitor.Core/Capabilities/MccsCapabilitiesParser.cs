using System.Globalization;

namespace SwitchMonitor.Core.Capabilities;

/// <summary>
/// Tolerant parser for MCCS capabilities strings such as
/// <c>(prot(monitor)type(LCD)model(X)cmds(01 02 03)vcp(10 12 14(05 08) 60(11 12 0F))mccs_ver(2.2))</c>.
/// Real monitors often omit the outer parentheses, leave parentheses unbalanced, pad with NULs,
/// or run hex bytes together ("0F1112"); all of those are accepted.
/// </summary>
public static class MccsCapabilitiesParser
{
    public static MonitorCapabilities Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return MonitorCapabilities.Empty;
        string text = raw.Replace("\0", "").Trim();
        string body = StripOuterParentheses(text);

        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < body.Length)
        {
            if (!IsKeyChar(body[i])) { i++; continue; }
            int keyStart = i;
            while (i < body.Length && IsKeyChar(body[i])) i++;
            string key = body[keyStart..i].ToLowerInvariant();
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            if (i >= body.Length || body[i] != '(') continue;
            int close = FindClose(body, i);
            string value = body[(i + 1)..close].Trim();
            sections.TryAdd(key, value);
            i = Math.Min(close + 1, body.Length);
        }

        var vcp = sections.TryGetValue("vcp", out var vcpText)
            ? ParseVcp(vcpText)
            : new Dictionary<byte, IReadOnlyList<int>>();
        return new MonitorCapabilities(text, sections, vcp);
    }

    internal static Dictionary<byte, IReadOnlyList<int>> ParseVcp(string text)
    {
        var codes = new Dictionary<byte, List<int>>();
        byte? last = null;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsAsciiHexDigit(c))
            {
                int start = i;
                while (i < text.Length && char.IsAsciiHexDigit(text[i])) i++;
                foreach (int code in SplitHexBytes(text.AsSpan(start, i - start)))
                {
                    last = (byte)code;
                    codes.TryAdd(last.Value, []);
                }
                continue;
            }
            if (c == '(')
            {
                int close = FindClose(text, i);
                if (last is byte owner)
                    foreach (int value in HexBytesIn(text.AsSpan(i + 1, close - i - 1)))
                        if (!codes[owner].Contains(value)) codes[owner].Add(value);
                i = Math.Min(close + 1, text.Length);
                continue;
            }
            i++;
        }
        return codes.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<int>)pair.Value.ToArray());
    }

    private static IEnumerable<int> HexBytesIn(ReadOnlySpan<char> text)
    {
        var values = new List<int>();
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsAsciiHexDigit(text[i])) { i++; continue; }
            int start = i;
            while (i < text.Length && char.IsAsciiHexDigit(text[i])) i++;
            values.AddRange(SplitHexBytes(text[start..i]));
        }
        return values;
    }

    /// <summary>"10" → [0x10]; "0F1112" → [0x0F, 0x11, 0x12]; malformed odd-length runs ("123") are ignored.</summary>
    private static List<int> SplitHexBytes(ReadOnlySpan<char> run)
    {
        var result = new List<int>();
        if (run.Length <= 2 || run.Length % 2 != 0)
        {
            if (run.Length <= 2 && int.TryParse(run, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int single))
                result.Add(single);
            return result;
        }
        for (int i = 0; i < run.Length; i += 2)
            result.Add(int.Parse(run.Slice(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return result;
    }

    private static string StripOuterParentheses(string text)
    {
        if (text.Length == 0 || text[0] != '(') return text;
        int close = FindClose(text, 0);
        // Balanced wrapper: "( ... )". Unbalanced: drop only the opening parenthesis.
        return close == text.Length - 1 ? text[1..^1] : text[1..];
    }

    /// <summary>
    /// Index of the parenthesis closing the one at <paramref name="open"/>,
    /// or <c>text.Length</c> when it is never closed (the group runs to the end).
    /// </summary>
    private static int FindClose(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return text.Length;
    }

    private static bool IsKeyChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}

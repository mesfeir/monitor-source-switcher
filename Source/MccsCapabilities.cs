using System.Globalization;

namespace MonitorSourceSwitcher;

/// <summary>
/// Parser for the MCCS capabilities string a monitor returns for VCP 0x00, for example:
///
///   (prot(monitor)type(lcd)model(AG241QG)cmds(01 02 03)vcp(02 03(01) 04 10 12)mccs_ver(2.2))
///
/// The vcp(...) block lists the codes the panel implements, and any code may carry the
/// values it accepts in brackets - which for 0x60 is the list of usable inputs. This is how
/// the app can tell "this panel cannot switch" apart from "the command failed".
/// </summary>
public sealed class MccsCapabilities
{
    private readonly Dictionary<byte, byte[]> _valueLists = new();

    public string Raw { get; }
    public string? Model { get; }
    public string? MccsVersion { get; }
    public HashSet<byte> SupportedCodes { get; } = new();

    private MccsCapabilities(string raw, string? model, string? mccsVersion)
    {
        Raw = raw;
        Model = model;
        MccsVersion = mccsVersion;
    }

    public bool SupportsInputSelect => SupportedCodes.Contains(Ddc.VcpInputSelect);

    /// <summary>The input values the panel advertises for 0x60, or empty when it lists none.</summary>
    public byte[] InputValues => _valueLists.TryGetValue(Ddc.VcpInputSelect, out var values) ? values : Array.Empty<byte>();

    public static MccsCapabilities? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var capabilities = new MccsCapabilities(raw, ExtractBlock(raw, "model"), ExtractBlock(raw, "mccs_ver"));

        var vcpBlock = ExtractBlock(raw, "vcp");
        if (vcpBlock is null) return capabilities;

        foreach (var token in vcpBlock.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var paren = token.IndexOf('(');
            var codeText = paren < 0 ? token : token[..paren];

            if (!byte.TryParse(codeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                continue;

            capabilities.SupportedCodes.Add(code);

            if (paren < 0) continue;

            var inner = token[(paren + 1)..].TrimEnd(')');
            var values = new List<byte>();
            foreach (var valueText in inner.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (byte.TryParse(valueText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    values.Add(value);

            if (values.Count > 0) capabilities._valueLists[code] = values.ToArray();
        }

        return capabilities;
    }

    /// <summary>
    /// Reads a bracketed block, counting nested brackets - values such as 14(03 05 09 0B)
    /// mean a naive "up to the first close bracket" would cut the block short.
    /// </summary>
    private static string? ExtractBlock(string raw, string key)
    {
        var start = raw.IndexOf(key + "(", StringComparison.Ordinal);
        if (start < 0) return null;

        start += key.Length + 1;
        var depth = 1;

        for (var i = start; i < raw.Length; i++)
        {
            if (raw[i] == '(') depth++;
            else if (raw[i] == ')' && --depth == 0) return raw[start..i];
        }

        return null;
    }
}

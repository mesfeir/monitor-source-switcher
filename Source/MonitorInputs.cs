namespace MonitorSourceSwitcher;

/// <summary>
/// The MCCS values a monitor accepts for VCP 0x60 (Input Select), with the spellings
/// people actually type.
/// </summary>
public static class MonitorInputs
{
    public sealed record Option(string Name, string[] Aliases, byte Value);

    public static readonly Option[] All =
    {
        new("vga",          new[] { "vga", "analog" },                                     0x01),
        new("dvi",          new[] { "dvi", "dvi1" },                                       0x03),
        new("dvi2",         new[] { "dvi2" },                                              0x04),
        new("displayport1", new[] { "displayport1", "displayport", "dp", "dp1" },          0x0F),
        new("displayport2", new[] { "displayport2", "dp2" },                               0x10),
        new("hdmi1",        new[] { "hdmi1", "hdmi" },                                     0x11),
        new("hdmi2",        new[] { "hdmi2" },                                             0x12),
        new("usbc",         new[] { "usbc", "usb-c", "type-c", "usbcdp" },                 0x1B),
    };

    /// <summary>Lower-cases and strips separators so "USB-C", "usb c" and "usbc" all match.</summary>
    public static string Normalize(string? value) =>
        new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static Option? Find(string? name)
    {
        var needle = Normalize(name);
        if (needle.Length == 0) return null;

        foreach (var option in All)
            foreach (var alias in option.Aliases)
                if (Normalize(alias) == needle)
                    return option;

        // A raw MCCS value such as "18" is accepted too.
        if (int.TryParse(needle, out var raw) && raw is > 0 and < 256)
            return new Option($"0x{raw:x2}", Array.Empty<string>(), (byte)raw);

        return null;
    }

    public static string? NameFor(byte value) =>
        All.FirstOrDefault(o => o.Value == value)?.Name;

    public static string SupportedList =>
        string.Join(", ", All.Select(o => o.Name));
}

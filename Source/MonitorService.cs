using System.Collections.Concurrent;

namespace MonitorSourceSwitcher;

public sealed record InputDto(string Name, byte Value);

public sealed record MonitorDto(
    int Id,
    string Description,
    string? Model,
    bool DdcResponds,
    bool? SupportsInputSelect,
    string? CurrentInput,
    int? CurrentInputValue,
    int? Brightness,
    int? BrightnessMax,
    IReadOnlyList<InputDto> Inputs,
    string Note,
    string? Capabilities);

public sealed class SwitchRequest
{
    // A class, not a record: Minimal API binding is happier with property setters.
    public string Input { get; set; } = "";
}

public sealed class SwitchResult
{
    /// <summary>True when the panel acknowledged the input-select command.</summary>
    public bool Ok { get; init; }

    public int Monitor { get; init; }
    public string Input { get; init; } = "";
    public int Value { get; init; }

    /// <summary>Whether the panel acknowledged the DDC/CI write.</summary>
    public bool Acknowledged { get; init; }

    /// <summary>Whether anything answers on this link at all. False means there is no DDC/CI here.</summary>
    public bool LinkCarriesDdc { get; init; }

    /// <summary>Taken from the panel's capabilities string. null when it does not provide one.</summary>
    public bool? InputSelectSupported { get; init; }

    /// <summary>
    /// Read-back comparison. null means the panel will not report its current input, which is
    /// the common case, so the change cannot be confirmed either way.
    /// </summary>
    public bool? Verified { get; init; }

    public string? Note { get; init; }
    public string? Error { get; init; }
}

public sealed class MonitorService
{
    public const string DefaultCycle = "displayport1,hdmi2";

    private static readonly ConcurrentDictionary<int, byte> LastSent = new();

    public List<MonitorDto> List()
    {
        using var session = Ddc.Session.Open();

        var result = new List<MonitorDto>();
        foreach (var monitor in session.Monitors)
        {
            var responds = session.TryGetVcp(monitor, Ddc.VcpBrightness, out var brightness, out var brightnessMax);
            var capabilities = session.TryGetCapabilities(monitor, out var raw)
                ? MccsCapabilities.Parse(raw)
                : null;

            var inputReadable = session.TryGetVcp(monitor, Ddc.VcpInputSelect, out var inputValue, out _);

            result.Add(new MonitorDto(
                Id: monitor.Id,
                Description: monitor.Description,
                Model: capabilities?.Model,
                DdcResponds: responds,
                SupportsInputSelect: capabilities?.SupportsInputSelect,
                CurrentInput: inputReadable ? MonitorInputs.NameFor((byte)inputValue) ?? inputValue.ToString() : null,
                CurrentInputValue: inputReadable ? (byte)inputValue : null,
                Brightness: responds ? (int)brightness : null,
                BrightnessMax: responds ? (int)brightnessMax : null,
                Inputs: OfferedInputs(capabilities),
                Note: Describe(responds, capabilities),
                Capabilities: capabilities?.Raw));
        }

        return result;
    }

    /// <summary>
    /// Prefers the inputs the panel advertises for VCP 0x60. Falls back to the full MCCS list
    /// when the panel will not say, so an uncooperative monitor is still switchable.
    /// </summary>
    private static List<InputDto> OfferedInputs(MccsCapabilities? capabilities)
    {
        // The panel told us it has no input select, so offering inputs would only invite
        // pointless calls. A panel that stays silent still gets the full list to try.
        if (capabilities is { SupportsInputSelect: false })
            return new List<InputDto>();

        var advertised = capabilities?.InputValues ?? Array.Empty<byte>();

        if (advertised.Length > 0)
            return advertised
                .Select(value => new InputDto(MonitorInputs.NameFor(value) ?? $"0x{value:x2}", value))
                .ToList();

        return MonitorInputs.All.Select(o => new InputDto(o.Name, o.Value)).ToList();
    }

    private static string Describe(bool responds, MccsCapabilities? capabilities)
    {
        if (!responds && capabilities is null)
            return "No DDC/CI on this link - the monitor answers nothing, so its source cannot be switched here. " +
                   "(Samsung Odyssey panels behave this way on DisplayPort: only their HDMI ports carry DDC/CI.)";

        if (capabilities is { SupportsInputSelect: false })
            return "DDC/CI works here, but this panel does not implement Input Select (VCP 0x60), " +
                   "so its source cannot be switched over DDC/CI.";

        if (capabilities is { SupportsInputSelect: true })
            return "Input Select (VCP 0x60) is supported on this link.";

        return "DDC/CI works here, but the panel does not report its capabilities, so input-select support is unknown.";
    }

    public SwitchResult Switch(int id, string inputName)
    {
        var option = MonitorInputs.Find(inputName);
        if (option is null)
        {
            return new SwitchResult
            {
                Ok = false,
                Monitor = id,
                Input = inputName,
                Error = $"Unknown input '{inputName}'. Supported: {MonitorInputs.SupportedList}, or a raw MCCS value such as 18."
            };
        }

        return Send(id, option);
    }

    /// <summary>
    /// Steps through a list of inputs, remembering the last one sent in memory.
    /// Best effort: the panel cannot be asked which input it is on, so changing the input on
    /// the monitor's own OSD makes this drift by one press rather than self-correcting.
    /// </summary>
    public SwitchResult Cycle(int id, string? inputs)
    {
        var names = (string.IsNullOrWhiteSpace(inputs) ? DefaultCycle : inputs)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (names.Length < 2)
        {
            return new SwitchResult
            {
                Ok = false,
                Monitor = id,
                Error = "cycle needs at least two inputs, for example inputs=displayport1,hdmi2"
            };
        }

        // Nothing sent yet: assume the first entry is the current input and move off it, so the
        // very first press actually changes something.
        var nextName = LastSent.TryGetValue(id, out var last)
            ? names[(Array.FindIndex(names, n => MonitorInputs.Find(n)?.Value == last) + 1) % names.Length]
            : names[1];

        var next = MonitorInputs.Find(nextName);
        return next is null
            ? new SwitchResult { Ok = false, Monitor = id, Error = $"Unknown input '{nextName}'. Supported: {MonitorInputs.SupportedList}." }
            : Send(id, next);
    }

    private static SwitchResult Send(int id, MonitorInputs.Option option)
    {
        using var session = Ddc.Session.Open();

        var monitor = session.ById(id);
        if (monitor is null)
        {
            return new SwitchResult
            {
                Ok = false,
                Monitor = id,
                Input = option.Name,
                Value = option.Value,
                Error = $"No monitor with id {id}. {session.Monitors.Count} monitor(s) present."
            };
        }

        var linkCarriesDdc = session.TryGetVcp(monitor, Ddc.VcpBrightness, out _, out _);
        var capabilities = session.TryGetCapabilities(monitor, out var raw) ? MccsCapabilities.Parse(raw) : null;

        var acknowledged = session.TrySetVcp(monitor, Ddc.VcpInputSelect, option.Value);
        if (acknowledged) LastSent[id] = option.Value;

        bool? verified = null;
        if (session.TryGetVcp(monitor, Ddc.VcpInputSelect, out var readBack, out _))
            verified = MonitorInputs.Normalize(MonitorInputs.NameFor((byte)readBack) ?? readBack.ToString())
                       == MonitorInputs.Normalize(option.Name);

        return new SwitchResult
        {
            Ok = acknowledged,
            Monitor = id,
            Input = option.Name,
            Value = option.Value,
            Acknowledged = acknowledged,
            LinkCarriesDdc = linkCarriesDdc,
            InputSelectSupported = capabilities?.SupportsInputSelect,
            Verified = verified,
            Note = acknowledged && verified is null
                ? "Accepted by the panel. It does not report its current input, so the change could not be confirmed by read-back."
                : null,
            Error = acknowledged ? null : ExplainFailure(linkCarriesDdc, capabilities)
        };
    }

    private static string ExplainFailure(bool linkCarriesDdc, MccsCapabilities? capabilities)
    {
        if (!linkCarriesDdc && capabilities is null)
            return "The monitor did not answer anything on this link, so there is no DDC/CI to switch with. " +
                   "(Samsung Odyssey panels implement DDC/CI on HDMI only - never on DisplayPort.)";

        if (capabilities is { SupportsInputSelect: false })
            return "This panel does not implement Input Select (VCP 0x60), so it rejected the command.";

        return "The panel did not acknowledge the input-select command. A few panels apply the change " +
               "without acknowledging it, so it is worth checking the screen.";
    }
}

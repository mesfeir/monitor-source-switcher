using System.Collections.Concurrent;

namespace MonitorSourceSwitcher;

public sealed record InputDto(string Name, byte Value);

public sealed record MonitorDto(
    int Id,
    string Description,
    string? Model,
    string Transport,
    string? TransportDetail,
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
    /// <summary>True when the switch was accepted - locally by the panel, or remotely by the helper machine.</summary>
    public bool Ok { get; init; }

    public int Monitor { get; init; }
    public string Input { get; init; } = "";
    public int Value { get; init; }

    /// <summary>"local" for this PC's own DDC/CI link, "mac" when another machine does the switching.</summary>
    public string Transport { get; init; } = MonitorService.LocalTransport;
    public string? TransportDetail { get; init; }

    public bool Acknowledged { get; init; }
    public bool LinkCarriesDdc { get; init; }
    public bool? InputSelectSupported { get; init; }

    /// <summary>Read-back comparison. Almost always null: this class of panel will not report its input.</summary>
    public bool? Verified { get; init; }

    public string? Note { get; init; }
    public string? Error { get; init; }
}

public sealed class MonitorService
{
    public const string LocalTransport = "local";
    public const string MacTransportName = "mac";
    public const string DefaultCycle = "displayport1,hdmi2";

    private static readonly ConcurrentDictionary<int, byte> LastSent = new();

    private readonly AppOptions _options;
    private readonly object _lock = new();
    private int _macFor = -1;

    public MonitorService(AppOptions options) => _options = options;

    public List<MonitorDto> List()
    {
        var monitors = ReadLocal();
        ApplyMacTarget(monitors);
        return monitors;
    }

    /// <summary>Everything this PC can see over its own DDC/CI links.</summary>
    private static List<MonitorDto> ReadLocal()
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
                Transport: LocalTransport,
                TransportDetail: null,
                DdcResponds: responds,
                SupportsInputSelect: capabilities?.SupportsInputSelect,
                CurrentInput: inputReadable ? MonitorInputs.NameFor((byte)inputValue) ?? inputValue.ToString() : null,
                CurrentInputValue: inputReadable ? (byte)inputValue : null,
                Brightness: responds ? (int)brightness : null,
                BrightnessMax: responds ? (int)brightnessMax : null,
                Inputs: OfferedInputs(responds, capabilities),
                Note: Describe(responds, capabilities),
                Capabilities: capabilities?.Raw));
        }

        return result;
    }

    /// <summary>
    /// Replaces the entry for the Mac-controlled monitor with one that actually has buttons.
    /// Without this the panel is reported but unusable, which is the honest state of the local
    /// link - but not of the monitor, which the other machine can switch perfectly well.
    /// </summary>
    private void ApplyMacTarget(List<MonitorDto> monitors)
    {
        var macFor = MacTargetId(monitors);
        if (macFor <= 0) return;

        var inputs = MacInputs();
        for (var i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].Id != macFor) continue;

            monitors[i] = monitors[i] with
            {
                Model = _options.Mac.DisplayName ?? monitors[i].Model,
                Transport = MacTransportName,
                TransportDetail = _options.Mac.Host,
                SupportsInputSelect = true,
                Inputs = inputs,
                Note = MacNote(monitors[i])
            };
            return;
        }

        // The panel is not attached to this PC at all, but the other machine can still switch
        // it - so present it rather than hiding the only thing that works.
        monitors.Add(new MonitorDto(
            Id: macFor,
            Description: $"Monitor {macFor} (via {_options.Mac.Host})",
            Model: _options.Mac.DisplayName,
            Transport: MacTransportName,
            TransportDetail: _options.Mac.Host,
            DdcResponds: false,
            SupportsInputSelect: true,
            CurrentInput: null,
            CurrentInputValue: null,
            Brightness: null,
            BrightnessMax: null,
            Inputs: inputs,
            Note: $"Not attached to this PC, but {_options.Mac.Host} is, and it can switch this monitor.",
            Capabilities: null));
    }

    private List<InputDto> MacInputs()
    {
        var inputs = new List<InputDto>();
        foreach (var name in _options.MacInputs)
            if (MonitorInputs.Find(name) is { } option)
                inputs.Add(new InputDto(option.Name, option.Value));
        return inputs;
    }

    private string MacNote(MonitorDto local)
    {
        var reason = local.DdcResponds
            ? "this panel does not implement Input Select on the link this PC is using"
            : "this panel's link to this PC carries no DDC/CI at all";

        return $"Switched through {_options.Mac.Host} over SSH, because {reason}. " +
               "That machine sits on one of this monitor's inputs, which does carry DDC/CI.";
    }

    /// <summary>
    /// Which local monitor id the helper machine controls. An explicit --mac-for wins;
    /// otherwise the monitor whose local link carries no DDC/CI is picked, because that is
    /// exactly the one a remote controller can rescue. Ambiguity is reported, never guessed.
    /// </summary>
    private int MacTargetId(List<MonitorDto>? local = null)
    {
        lock (_lock)
        {
            if (_macFor >= 0) return _macFor;

            if (!_options.Mac.IsConfigured) { _macFor = 0; return 0; }

            if (_options.MacFor > 0)
            {
                _macFor = _options.MacFor;
                Console.WriteLine($"Mac transport: monitor {_macFor} is switched via {_options.Mac.Host} (from --mac-for).");
                return _macFor;
            }

            var dead = (local ?? ReadLocal())
                .Where(m => !m.DdcResponds && m.SupportsInputSelect is null)
                .Select(m => m.Id)
                .ToList();

            if (dead.Count == 1)
            {
                _macFor = dead[0];
                Console.WriteLine($"Mac transport: monitor {_macFor} has no local DDC/CI, so it is switched via {_options.Mac.Host}.");
            }
            else
            {
                _macFor = 0;
                Console.WriteLine(dead.Count == 0
                    ? "Mac transport: no monitor needs it (every panel answers locally) - not used."
                    : $"Mac transport: {dead.Count} monitors have no local DDC/CI, so the choice is ambiguous - pass --mac-for <id> to pick one.");
            }

            return _macFor;
        }
    }

    /// <summary>
    /// The inputs worth offering. Buttons appear only when switching is actually possible, so
    /// the UI never presents a menu of guaranteed failures.
    /// </summary>
    private static List<InputDto> OfferedInputs(bool responds, MccsCapabilities? capabilities)
    {
        if (capabilities is { SupportsInputSelect: false })
            return new List<InputDto>();

        if (capabilities is null && !responds)
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

    private SwitchResult Send(int id, MonitorInputs.Option option)
    {
        if (MacTargetId() == id && id > 0)
            return SendViaMac(id, option);

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
            Transport = LocalTransport,
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

    /// <summary>
    /// Hands the switch to the other machine. Its exit code is a real success signal: m1ddc
    /// exits 0 only when the panel accepted the command, and non-zero otherwise.
    /// </summary>
    private SwitchResult SendViaMac(int id, MonitorInputs.Option option)
    {
        var result = _options.Mac.SetInput(option.Value);
        if (result.Ok) LastSent[id] = option.Value;

        return new SwitchResult
        {
            Ok = result.Ok,
            Monitor = id,
            Input = option.Name,
            Value = option.Value,
            Transport = MacTransportName,
            TransportDetail = _options.Mac.Host,
            Acknowledged = result.Ok,
            LinkCarriesDdc = true,
            InputSelectSupported = true,
            Verified = null,
            Note = result.Ok
                ? $"{_options.Mac.Host} reported success (exit 0). This panel cannot report which input is live, so read-back is still unavailable."
                : null,
            Error = result.Ok ? null : $"Switching via {_options.Mac.Host} failed: {result.Detail}"
        };
    }

    private static string ExplainFailure(bool linkCarriesDdc, MccsCapabilities? capabilities)
    {
        if (!linkCarriesDdc && capabilities is null)
            return "The monitor did not answer anything on this link, so there is no DDC/CI to switch with. " +
                   "(Samsung Odyssey panels implement DDC/CI on HDMI only - never on DisplayPort.) " +
                   "If another machine is on one of this monitor's inputs, point the app at it with --mac.";

        if (capabilities is { SupportsInputSelect: false })
            return "This panel does not implement Input Select (VCP 0x60), so it rejected the command.";

        return "The panel did not acknowledge the input-select command. A few panels apply the change " +
               "without acknowledging it, so it is worth checking the screen.";
    }
}

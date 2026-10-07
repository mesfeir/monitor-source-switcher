namespace MonitorSourceSwitcher;

/// <summary>Everything the app is told on the command line, in one place.</summary>
public sealed class AppOptions
{
    public int Port { get; set; } = 8152;
    public string Host { get; set; } = "127.0.0.1";
    public string? Token { get; set; }
    public bool Open { get; set; }

    /// <summary>Optional second machine that can switch a monitor this PC cannot reach.</summary>
    public MacTransport Mac { get; } = new();

    /// <summary>
    /// Local monitor id that the Mac controls. 0 means "work it out": the monitor whose local
    /// link carries no DDC/CI is exactly the one that needs a remote controller, so if exactly
    /// one qualifies it is used. Ambiguity is reported, never guessed at.
    /// </summary>
    public int MacFor { get; set; }

    /// <summary>Inputs offered on the Mac-routed monitor. Default suits a PC on DP and a Mac on HDMI 2.</summary>
    public string[] MacInputs { get; set; } = { "displayport1", "hdmi2" };
}

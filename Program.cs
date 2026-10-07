using System.Diagnostics;
using MonitorSourceSwitcher;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Monitor Source Switcher drives DDC/CI through dxva2.dll and only runs on Windows.");
    return 1;
}

var options = new AppOptions();

for (var i = 0; i < args.Length; i++)
{
    var a = args[i];

    if (a is "-h" or "--help") { PrintHelp(); return 0; }
    if (a == "--open") { options.Open = true; continue; }
    if (a == "--port" && NextInt(args, ref i, out var port)) { options.Port = port; continue; }
    if (a == "--host" && NextString(args, ref i, out var host)) { options.Host = host; continue; }
    if (a == "--token" && NextString(args, ref i, out var token)) { options.Token = token; continue; }

    if (a == "--mac" && NextString(args, ref i, out var macHost)) { options.Mac.Host = macHost; continue; }
    if (a == "--mac-key" && NextString(args, ref i, out var macKey)) { options.Mac.KeyPath = ExpandHome(macKey); continue; }
    if (a == "--mac-for" && NextInt(args, ref i, out var macFor)) { options.MacFor = macFor; continue; }
    if (a == "--mac-inputs" && NextString(args, ref i, out var macInputs)) { options.MacInputs = Split(macInputs); continue; }
    if (a == "--mac-display" && NextInt(args, ref i, out var macDisplay)) { options.Mac.Display = macDisplay; continue; }
    if (a == "--m1ddc" && NextString(args, ref i, out var m1ddc)) { options.Mac.M1ddcPath = m1ddc; continue; }

    Console.Error.WriteLine($"Unknown or incomplete argument: {a}");
    PrintHelp();
    return 1;
}

// Pass no args to the builder so our own flags are not read as configuration keys.
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<MonitorService>();

var app = builder.Build();

if (options.Token is not null)
{
    // Only enforced when a token is configured, which is only worth doing when binding beyond
    // localhost. The bundled web UI calls the same API, so pass ?token= to it.
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            var supplied = ctx.Request.Headers["X-Api-Token"].FirstOrDefault()
                           ?? ctx.Request.Query["token"].FirstOrDefault();

            if (!string.Equals(supplied, options.Token, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "Missing or invalid API token." });
                return;
            }
        }

        await next();
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    app = "Monitor Source Switcher",
    version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
    platform = "windows",
    macTransport = options.Mac.IsConfigured ? options.Mac.Host : null
}));

app.MapGet("/api/inputs", () =>
    Results.Ok(MonitorInputs.All.Select(o => new { o.Name, value = o.Value, hex = $"0x{o.Value:x2}" })));

app.MapGet("/api/monitors", (MonitorService service) => Results.Ok(service.List()));

app.MapPost("/api/monitors/{id:int}/input", (int id, SwitchRequest request, MonitorService service) =>
{
    if (string.IsNullOrWhiteSpace(request.Input))
        return Results.BadRequest(new { ok = false, error = "Body must be JSON like {\"input\":\"hdmi2\"}." });

    var result = service.Switch(id, request.Input);
    return result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status409Conflict);
});

// Convenience form that works straight from a URL, a Stream Deck action, or a browser tab.
app.MapPost("/api/monitors/{id:int}/input/{name}", (int id, string name, MonitorService service) =>
{
    var result = service.Switch(id, name);
    return result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status409Conflict);
});

app.MapPost("/api/monitors/{id:int}/cycle", (int id, string? inputs, MonitorService service) =>
{
    var result = service.Cycle(id, inputs);
    return result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status409Conflict);
});

var listenUrl = $"http://{(options.Host is "0.0.0.0" or "+" or "*" ? "localhost" : options.Host)}:{options.Port}";
Console.WriteLine($"Monitor Source Switcher - {listenUrl}");
if (options.Token is not null) Console.WriteLine("API token required on /api/* (send X-Api-Token header).");
if (options.Host is "0.0.0.0" or "+" or "*")
    Console.WriteLine("WARNING: bound beyond localhost. Anyone who can reach this port can switch your monitors.");

if (options.Mac.IsConfigured)
{
    if (string.IsNullOrWhiteSpace(options.Mac.KeyPath))
        options.Mac.KeyPath = FindDefaultKey();

    if (!File.Exists(options.Mac.KeyPath))
    {
        Console.WriteLine($"Mac transport: no SSH key at {options.Mac.KeyPath} - switching through it will fail.");
    }
    else
    {
        // Probe once at startup: this validates ssh, the key and m1ddc in one go, and gives the
        // monitor its real name instead of the generic "Generic PnP Monitor".
        var (reachable, displayName, detail) = options.Mac.Probe();
        if (reachable)
        {
            options.Mac.DisplayName = displayName;
            Console.WriteLine($"Mac transport: {options.Mac.Host} reachable, drives \"{displayName ?? "an unnamed display"}\".");
        }
        else
        {
            Console.WriteLine($"Mac transport: could not reach {options.Mac.Host} - {detail}");
            Console.WriteLine("  Switches through it will keep reporting that error until it answers.");
        }
    }
}

var service = app.Services.GetRequiredService<MonitorService>();
try
{
    var monitors = service.List();
    Console.WriteLine($"{monitors.Count} monitor(s):");
    foreach (var m in monitors)
    {
        var transport = m.Transport == MonitorService.MacTransportName ? $"  via {m.TransportDetail}" : "";
        var switchable = m.Inputs.Count > 0 ? $"{m.Inputs.Count} input(s)" : "nothing switchable";
        Console.WriteLine($"  [{m.Id}] {m.Model ?? m.Description}{transport}  -  {switchable}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Could not enumerate monitors up front: {ex.Message}");
}

if (options.Open) Process.Start(new ProcessStartInfo(listenUrl) { UseShellExecute = true });

app.Run();
return 0;

static bool NextString(string[] args, ref int i, out string value)
{
    if (i + 1 >= args.Length) { value = ""; return false; }
    value = args[++i];
    return true;
}

static bool NextInt(string[] args, ref int i, out int value)
{
    value = 0;
    if (i + 1 >= args.Length) return false;
    if (!int.TryParse(args[i + 1], out value)) return false;
    i++;
    return true;
}

static string[] Split(string list) =>
    list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

static string ExpandHome(string path)
{
    if (!path.StartsWith('~')) return path;
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(home, path.TrimStart('~').TrimStart('/', '\\'));
}

static string FindDefaultKey()
{
    var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
    foreach (var name in new[] { "id_ed25519", "id_ed25519_sk", "id_rsa", "id_ecdsa" })
    {
        var candidate = Path.Combine(ssh, name);
        if (File.Exists(candidate)) return candidate;
    }

    // Nothing found: name the most likely path so the message is concrete.
    return Path.Combine(ssh, "id_ed25519");
}

static void PrintHelp()
{
    Console.WriteLine("""
        Monitor Source Switcher - switch monitor input sources over DDC/CI.

        Usage:
          MonitorSourceSwitcher [options]

        Local (this PC's own DDC/CI links):
          --port  <n>          Port to listen on (default 8152)
          --host  <h>          Address to bind (default 127.0.0.1; use 0.0.0.0 for LAN access)
          --token <t>          Require this value in the X-Api-Token header on /api/*
          --open               Open the web UI in the default browser on start
          -h, --help           Show this help

        Remote helper (a second machine sitting on a monitor this PC cannot reach):
          --mac <user@host>    e.g. --mac melsfeir@192.168.1.204
          --mac-for <id>       Local monitor id that machine controls (default: the one whose
                               local link carries no DDC/CI, if exactly one does)
          --mac-inputs <list>  Inputs to offer there (default displayport1,hdmi2)
          --mac-key  <path>    SSH identity file (default ~/.ssh/id_ed25519)
          --mac-display <n>    Remote display selector, if that machine has several
          --m1ddc <path>       Remote m1ddc path (default /opt/homebrew/bin/m1ddc)

        Inputs: vga, dvi, dvi2, displayport1, displayport2, hdmi1, hdmi2, usbc, or a raw MCCS value.
        """);
}

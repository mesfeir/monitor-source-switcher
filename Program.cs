using System.Diagnostics;
using MonitorSourceSwitcher;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Monitor Source Switcher drives DDC/CI through dxva2.dll and only runs on Windows.");
    return 1;
}

var port = 8152;
var host = "127.0.0.1";
string? token = null;
var openBrowser = false;

for (var i = 0; i < args.Length; i++)
{
    var a = args[i];

    if (a is "-h" or "--help") { PrintHelp(); return 0; }
    if (a == "--open") { openBrowser = true; continue; }
    if (a == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) { port = p; i++; continue; }
    if (a == "--host" && i + 1 < args.Length) { host = args[++i]; continue; }
    if (a == "--token" && i + 1 < args.Length) { token = args[++i]; continue; }

    Console.Error.WriteLine($"Unknown or incomplete argument: {a}");
    PrintHelp();
    return 1;
}

// Pass no args to the builder so our own flags are not read as configuration keys.
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.WebHost.UseUrls($"http://{host}:{port}");
builder.Services.AddSingleton<MonitorService>();

var app = builder.Build();

if (token is not null)
{
    // Only enforced when a token is configured, which is only worth doing when binding
    // beyond localhost. The bundled web UI calls the same API, so pass ?token= to it.
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            var supplied = ctx.Request.Headers["X-Api-Token"].FirstOrDefault()
                           ?? ctx.Request.Query["token"].FirstOrDefault();

            if (!string.Equals(supplied, token, StringComparison.Ordinal))
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
    platform = "windows"
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

var listenUrl = $"http://{(host is "0.0.0.0" or "+" or "*" ? "localhost" : host)}:{port}";
Console.WriteLine($"Monitor Source Switcher - {listenUrl}");
if (token is not null) Console.WriteLine("API token required on /api/* (send X-Api-Token header).");
if (host is "0.0.0.0" or "+" or "*")
    Console.WriteLine("WARNING: bound beyond localhost. Anyone who can reach this port can switch your monitors.");

try
{
    using var probe = Ddc.Session.Open();
    Console.WriteLine($"{probe.Monitors.Count} monitor(s):");
    foreach (var m in probe.Monitors)
        Console.WriteLine($"  [{m.Id}] {m.Description}");
}
catch (Exception ex)
{
    Console.WriteLine($"Could not enumerate monitors up front: {ex.Message}");
}

if (openBrowser) Process.Start(new ProcessStartInfo(listenUrl) { UseShellExecute = true });

app.Run();
return 0;

static void PrintHelp()
{
    Console.WriteLine("""
        Monitor Source Switcher - switch monitor input sources over DDC/CI.

        Usage:
          MonitorSourceSwitcher [options]

        Options:
          --port  <n>      Port to listen on (default 8152)
          --host  <h>      Address to bind (default 127.0.0.1; use 0.0.0.0 for LAN access)
          --token <t>      Require this value in the X-Api-Token header on /api/*
          --open           Open the web UI in the default browser on start
          -h, --help       Show this help

        Inputs: vga, dvi, dvi2, displayport1, displayport2, hdmi1, hdmi2, usbc, or a raw MCCS value.
        """);
}

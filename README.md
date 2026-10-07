# Monitor Source Switcher

Switch a monitor's **input source** — DisplayPort, HDMI, USB-C — over DDC/CI, from a small
local app with a web UI and an HTTP API. Point a Stream Deck button, a script or a cron job at
it and the monitor changes input.

No third-party tools, no vendor SDK, no bundled binaries. DDC/CI is exposed by `dxva2.dll`, a
DLL that already ships with Windows, so this talks to your monitors directly. Nothing like
ControlMyMonitor or ddcutil is required.

```
POST /api/monitors/1/input/hdmi2
```

## Why it tells you *why* a switch failed

Most DDC tools only report that nothing happened. Monitors vary enormously in what they expose,
so this asks each panel for its **MCCS capabilities string** (VCP `0x00`) and reports what it can
actually do. Real output from a two-monitor PC:

| Monitor | Link | Result |
| --- | --- | --- |
| Samsung Odyssey | DisplayPort | Answers nothing at all → **no DDC/CI on this link**, so nothing can be switched through it |
| AOC AG241QG | DisplayPort | DDC/CI works (brightness readable), but its capabilities string lists no `60` → **the panel does not implement Input Select** |

That distinction is the whole point. "The command failed" is useless; "this panel cannot do
this, and here is the string that proves it" tells you whether to change a setting, change a
cable, or change your expectations.

## Requirements

- **Windows** (uses the Windows DDC/CI API through `dxva2.dll`)
- **.NET 9 SDK** to build, or the matching runtime to run a published build
- Monitors with **DDC/CI enabled** — it is on by default on most panels, but some have a
  "DDC/CI" entry in their OSD menu you may need to switch on

## Quick start

```bash
git clone https://github.com/mesfeir/monitor-source-switcher
cd monitor-source-switcher
dotnet run
```

Then open <http://127.0.0.1:8152> and press a button.

### Launching it without a terminal

`start.cmd` in the repository root is a double-clickable launcher — put a shortcut to it
anywhere convenient. It starts the app and opens the UI, and pressing it again while the app
is already listening just reopens the UI instead of failing on a busy port.

The app runs *in that window*, so the log is visible there and closing the window is how you
stop it. It uses the app's own `--open` flag to open the UI once Kestrel has actually bound,
rather than sleeping and hoping the port came up in time.

To produce a self-contained executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

### Options

| Option | Default | Meaning |
| --- | --- | --- |
| `--port <n>` | `8152` | Port to listen on |
| `--host <h>` | `127.0.0.1` | Address to bind. Use `0.0.0.0` for LAN access |
| `--token <t>` | *(off)* | Require this value in the `X-Api-Token` header on `/api/*` |
| `--open` | *(off)* | Open the web UI in the default browser on start |

The server binds to localhost only by default, so it is not reachable from your network until
you ask for it. If you do bind to `0.0.0.0`, set `--token` as well — otherwise anyone on your
network can switch your monitors.

## HTTP API

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/health` | Liveness, app name, version |
| `GET` | `/api/monitors` | Every monitor with its DDC status, capabilities, inputs and a plain-English note |
| `GET` | `/api/inputs` | The MCCS input values this app knows about |
| `POST` | `/api/monitors/{id}/input` | Switch using a JSON body: `{"input":"hdmi2"}` |
| `POST` | `/api/monitors/{id}/input/{name}` | Switch straight from the URL |
| `POST` | `/api/monitors/{id}/cycle` | Step through inputs — `?inputs=displayport1,hdmi2` |

Accepted input names: `vga`, `dvi`, `dvi2`, `displayport1` (aliases `displayport`, `dp`, `dp1`),
`displayport2` (`dp2`), `hdmi1` (`hdmi`), `hdmi2`, `usbc` (`usb-c`, `type-c`). A raw MCCS value
such as `18` also works.

```bash
# what have I got?
curl http://127.0.0.1:8152/api/monitors

# switch monitor 1 to HDMI 2
curl -X POST http://127.0.0.1:8152/api/monitors/1/input/hdmi2

# flip back and forth between the PC and a Mac on one button
curl -X POST "http://127.0.0.1:8152/api/monitors/1/cycle?inputs=displayport1,hdmi2"

# with a token configured
curl -X POST -H "X-Api-Token: $TOKEN" http://127.0.0.1:8152/api/monitors/1/input/hdmi2
```

Status codes: **200** the panel acknowledged the switch, **409** the panel refused it (the body
carries the reason), **400** the request body was not usable.

### Response

```json
{
  "ok": true,
  "monitor": 1,
  "input": "hdmi2",
  "value": 18,
  "acknowledged": true,
  "linkCarriesDdc": true,
  "inputSelectSupported": true,
  "verified": null,
  "note": "Accepted by the panel. It does not report its current input, so the change could not be confirmed by read-back.",
  "error": null
}
```

`verified` is `null` on most monitors — they simply will not report which input is live, so
there is nothing to check the result against. `acknowledged` is the panel's own answer to the
DDC/CI write, which is a real signal: it comes back `true` for a VCP code the panel implements
and `false` for one it does not.

## Stream Deck

Two ways, both dependency-free:

- **Website action** — point it at `http://127.0.0.1:8152/api/monitors/1/input/hdmi2`. It will
  open a tab with the JSON result; use an Open action with a hidden launcher if you want it
  silent.
- **Open action → a one-line `.cmd`** — no console flash if you wrap it in a `.vbs`, and no
  file picker problems (never point a Stream Deck Open action at a `.ps1`: `.ps1` has no file
  association by default on Windows, so the action silently does nothing).

```bat
@echo off
curl -s -X POST http://127.0.0.1:8152/api/monitors/1/input/hdmi2 >nul
```

## Known limits

- **The monitor decides.** Input select can only work when the link carries DDC/CI *and* the
  panel implements VCP `0x60`. Many Samsung Odyssey monitors implement DDC/CI on their HDMI
  ports **only** and never on DisplayPort — on those, a DisplayPort cable cannot switch them,
  no matter what software you run. Check `supportsInputSelect` in `/api/monitors` before
  blaming the app.
- **Monitors rarely report their current input**, so the app cannot tell you which input is
  live. `cycle` remembers what it last sent, so changing the input on the monitor's own OSD
  makes it drift by one press instead of self-correcting.
- **Monitor ids follow Windows' display order** and can change when displays are plugged in or
  removed. Descriptions are generic (`Generic PnP Monitor`) on many setups, which is why the
  capabilities string is echoed back — it usually carries the real model name.
- **A few panels apply a change without acknowledging it.** The acknowledgement is treated as
  authoritative here because that matched measurements on real hardware, but if a switch seems
  to work while the API reports failure, that is the case you are in.
- Windows only, by design. The equivalent call on macOS is a different (private) API.

## Development

```
Program.cs                 minimal API, CLI options, optional token check
Source/Ddc.cs              dxva2 DDC/CI interop: enumerate monitors, read/write VCP codes
Source/MccsCapabilities.cs parses the capabilities string from VCP 0x00
Source/MonitorInputs.cs    MCCS input values and the names people type
Source/MonitorService.cs   the operations the API exposes
wwwroot/index.html         the web UI, a plain client of the API
```

`Source/Ddc.cs` is the only part that touches native code, and the only Windows-only part.

## License

MIT — see [LICENSE](LICENSE).

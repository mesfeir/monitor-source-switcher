using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MonitorSourceSwitcher;

/// <summary>
/// Switches a monitor by asking another machine to do it, over SSH.
///
/// A monitor whose local link carries no DDC/CI cannot be switched from this PC by any
/// software - the control channel simply is not there. But if a second machine sits on one of
/// that monitor's inputs, *its* link does carry DDC/CI. So this transport hands the job to
/// that machine: ssh in and run m1ddc there.
///
/// Two details cost real debugging time and are handled explicitly here:
///
///  * Non-interactive SSH gets a bare PATH (/usr/bin:/bin:/usr/sbin:/sbin), so Homebrew's
///    /opt/homebrew/bin is NOT on it and a bare `m1ddc` is "command not found". Absolute path.
///  * ssh must never be allowed to prompt (no key passphrase prompt, no host-key question,
///    no password prompt), or the app blocks forever waiting on a stdin nobody is watching.
///    Hence BatchMode and accept-new.
/// </summary>
public sealed class MacTransport
{
    public string Host { get; set; } = "";
    public string KeyPath { get; set; } = "";
    public string M1ddcPath { get; set; } = "/opt/homebrew/bin/m1ddc";

    /// <summary>Optional remote display selector. m1ddc's form is `display N <verb>`, not `-d N`.</summary>
    public int? Display { get; set; }

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Name of the display m1ddc reports, filled in at startup. Used for labelling.</summary>
    public string? DisplayName { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    public sealed record Result(bool Ok, int ExitCode, string Output, string Error)
    {
        /// <summary>The most useful line to show a user: stderr if there is any, else stdout.</summary>
        public string Detail => string.IsNullOrWhiteSpace(Error) ? Output : Error;
    }

    /// <summary>
    /// Close the requested input on the remote machine. m1ddc's exit code is trustworthy -
    /// a bad value exits non-zero - so it is a real success signal, unlike anything the local
    /// DisplayPort link could offer.
    /// </summary>
    public Result SetInput(byte value)
    {
        if (!IsConfigured) return new Result(false, -1, "", "No Mac host configured.");

        var remote = Display is null
            ? $"{M1ddcPath} set input {value}"
            : $"{M1ddcPath} display {Display} set input {value}";

        return Run(remote);
    }

    /// <summary>Asks the remote which display it can drive, so the panel can be named properly.</summary>
    public (bool Ok, string? Name, string Raw) Probe()
    {
        if (!IsConfigured) return (false, null, "");

        var result = Run($"{M1ddcPath} display list");
        return (result.Ok, ParseDisplayName(result.Output), result.Ok ? result.Output : result.Detail);
    }

    /// <summary>Parses m1ddc's `display list` line: "[1] LS28AG700N (A6184AB2-...)".</summary>
    public static string? ParseDisplayName(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var match = Regex.Match(output, @"\[\d+\]\s+(.+?)\s*\(");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private Result Run(string remoteCommand)
    {
        var psi = new ProcessStartInfo("ssh.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Never prompt. A prompt here is a hang, not a question.
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=6");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");

        if (!string.IsNullOrWhiteSpace(KeyPath) && File.Exists(KeyPath))
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(KeyPath);
        }

        psi.ArgumentList.Add(Host);

        // One argument: ssh joins everything after the host and hands it to the remote shell.
        psi.ArgumentList.Add(remoteCommand);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return new Result(false, -1, "", "Could not start ssh.exe.");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeoutSeconds * 1000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new Result(false, -1, "", $"ssh to {Host} timed out after {TimeoutSeconds}s.");
            }

            return new Result(
                process.ExitCode == 0,
                process.ExitCode,
                stdout.GetAwaiter().GetResult().Trim(),
                stderr.GetAwaiter().GetResult().Trim());
        }
        catch (Exception ex)
        {
            return new Result(false, -1, "", $"Could not run ssh.exe: {ex.Message}");
        }
    }
}

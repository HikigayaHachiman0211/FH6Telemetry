using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FH6Telemetry.Input;

/// <summary>Temporarily enables the isolation already paired with this portable installation.</summary>
internal sealed class HidHideRelaySession : IDisposable
{
    private readonly string _cli;
    private readonly string[] _pairedDevices;
    private readonly FileStream _lease;
    private bool _restoreVisibility;
    private string? _temporaryApplication;

    private HidHideRelaySession(string cli, string[] devices, FileStream lease)
    {
        _cli = cli;
        _pairedDevices = devices;
        _lease = lease;
        _restoreVisibility = lease.Length != 0;
    }

    internal static bool IsPaired => PairingPath() is not null;

    internal static HidHideRelaySession? Activate()
    {
        var pairing = PairingPath();
        if (pairing is null) return null;
        var session = Open(pairing);
        try
        {
            var state = session.GetMatchingState();
            const string allow = "--app-reg ";
            if (state.Where(line => line.StartsWith(allow, StringComparison.Ordinal))
                .Select(line => Path.GetFileName(line[allow.Length..].Trim('"')))
                .Any(name => string.Equals(name, "steam.exe", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(name, "forzahorizon6.exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(TelemetryText.Get("HidHideSteamOrGameInWhitelist"));
            var processPath = Environment.ProcessPath ??
                throw new InvalidOperationException(TelemetryText.Get("HidHideNoAppPath"));
            if (!state.Contains($"--app-reg \"{processPath}\"", StringComparer.OrdinalIgnoreCase))
                session._temporaryApplication = processPath;
            if (state.Contains("--cloak-off", StringComparer.Ordinal))
            {
                // Keep ownership on disk so a subsequent launch can recover from an interrupted session.
                session._lease.SetLength(1);
                session._lease.Flush(flushToDisk: true);
                session._restoreVisibility = true;
                session.Invoke("--app-reg", processPath, "--cloak-on");
            }
            else
            {
                session.Invoke("--app-reg", processPath);
            }
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal static void RestoreInterrupted()
    {
        var pairing = PairingPath();
        if (pairing is null || !File.Exists(pairing + ".lease")) return;
        using var session = Open(pairing);
        session.RestoreVisibility();
    }

    private static string? PairingPath()
    {
        var launcherDirectory = Environment.GetEnvironmentVariable("FH6_TELEMETRY_LAUNCH_DIR");
        if (string.IsNullOrWhiteSpace(launcherDirectory)) return null;
        var file = Path.Combine(launcherDirectory, "Data", "fh6-hidhide.json");
        return File.Exists(file) ? file : null;
    }

    private static HidHideRelaySession Open(string pairing)
    {
        var devices = JsonSerializer.Deserialize<string[]>(File.ReadAllText(pairing));
        if (devices is not { Length: > 0 } ||
            devices.Any(device => device is null ||
                !(device.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase) ||
                  device.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))) ||
            devices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != devices.Length)
            throw new InvalidOperationException(TelemetryText.Get("HidHideInvalidPairing"));

        var basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Nefarius Software Solutions", "HidHide");
        var cli = Path.Combine(basePath, "HidHideCLI.exe");
        if (!File.Exists(cli)) cli = Path.Combine(basePath, "x64", "HidHideCLI.exe");
        if (!File.Exists(cli))
            throw new InvalidOperationException(TelemetryText.Get("HidHideMissingCli"));
        try
        {
            return new HidHideRelaySession(cli, devices,
                new FileStream(pairing + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(TelemetryText.Get("HidHideBusyOtherWindow"), ex);
        }
    }

    private string[] GetMatchingState()
    {
        var state = Invoke("--cloak-state", "--inv-state", "--dev-list", "--app-list");
        const string hide = "--dev-hide ";
        var devices = state.Where(line => line.StartsWith(hide, StringComparison.Ordinal))
            .Select(line => line[hide.Length..].Trim('"')).ToArray();
        if (!state.Contains("--inv-off", StringComparer.Ordinal) ||
            (!state.Contains("--cloak-on", StringComparer.Ordinal) && !state.Contains("--cloak-off", StringComparer.Ordinal)) ||
            devices.Length != _pairedDevices.Length ||
            devices.Except(_pairedDevices, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidOperationException(TelemetryText.Get("HidHideDevicesChanged"));
        return state;
    }

    private void RestoreVisibility()
    {
        if (!_restoreVisibility) return;
        var state = GetMatchingState();
        if (state.Contains("--cloak-on", StringComparer.Ordinal))
            Invoke("--cloak-off");
        _lease.SetLength(0);
        _lease.Flush(flushToDisk: true);
        _restoreVisibility = false;
    }

    private string[] Invoke(params string[] arguments)
    {
        var start = new ProcessStartInfo(_cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException(TelemetryText.Get("HidHideCannotStartCli"));
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(3500))
        {
            try { process.Kill(); }
            catch (InvalidOperationException) { }
            throw new InvalidOperationException(TelemetryText.Get("HidHideTimeout"));
        }
        var error = errors.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0 || error.Length > 0)
            throw new InvalidOperationException(TelemetryText.Format("HidHideFailed", error));
        return output.GetAwaiter().GetResult()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public void Dispose()
    {
        try
        {
            RestoreVisibility();
            if (_temporaryApplication is string path) Invoke("--app-unreg", path);
        }
        catch (Exception error) { Debug.WriteLine(error); }
        finally { _lease.Dispose(); }
    }
}

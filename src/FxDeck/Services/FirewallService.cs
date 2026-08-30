using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace FxDeck.Services;

public enum FirewallAllowOutcome
{
    Added,

    /// <summary>The user dismissed the UAC prompt.</summary>
    Cancelled,

    Failed,
}

/// <param name="RuleExists">Any inbound rule named FxDeck or bound to this executable.</param>
/// <param name="PortAllowed">An enabled allow rule covers the port and no block rule overrides it.</param>
/// <param name="Blocked">An enabled block rule matches (Windows creates one when the first-run prompt is cancelled).</param>
public sealed record FirewallStatus(bool RuleExists, bool PortAllowed, bool Blocked, int Port);

public sealed record FirewallAllowResult(FirewallAllowOutcome Outcome, string? Message);

/// <summary>
/// Windows Firewall inbound rule for the deck port (design memo §3.5).
/// Status is read through the firewall COM API (no elevation, no localized text to parse);
/// the change runs netsh elevated with a fixed command line plus a validated integer — never text from the browser.
/// </summary>
public sealed class FirewallService
{
    public const string RuleName = "FxDeck";
    private const int ErrorCancelled = 1223;
    private const int DirectionIn = 1;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;
    private const int ProtocolTcp = 6;
    private const int ProtocolAny = 256;

    private readonly ILogger<FirewallService> _logger;
    private readonly string? _executable;

    public FirewallService(ILogger<FirewallService> logger)
    {
        _logger = logger;
        _executable = Environment.ProcessPath;
    }

    public static string BuildAddRuleArguments(int port)
    {
        ValidatePort(port);
        return $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port}";
    }

    public static string BuildDeleteRuleArguments() => $"advfirewall firewall delete rule name=\"{RuleName}\" dir=in";

    /// <summary>
    /// One elevated cmd: drop every inbound FxDeck rule (including the program rules Windows created from the
    /// first-run prompt — a leftover block rule would override our allow rule), then add the port rule.
    /// </summary>
    public static string BuildAllowCommandLine(int port) => $"/c netsh {BuildDeleteRuleArguments()} & netsh {BuildAddRuleArguments(port)}";

    /// <summary>Reads the current rules for this app without elevation.</summary>
    public Task<FirewallStatus> GetStatusAsync(int port, CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        return Task.Run(() =>
        {
            try
            {
                var rules = ReadRules();
                return Evaluate(rules, port, _executable);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                _logger.LogWarning("Could not query the firewall: {Message}", ex.Message);
                return new FirewallStatus(false, false, false, port);
            }
        }, cancellationToken);
    }

    /// <summary>A rule as far as we care (also the shape used by the unit tests).</summary>
    public sealed record Rule(string Name, string? ApplicationName, bool Enabled, int Direction, int Action, int Protocol, string? LocalPorts);

    public static FirewallStatus Evaluate(IEnumerable<Rule> rules, int port, string? executable)
    {
        var exists = false;
        var allowed = false;
        var blocked = false;
        foreach (var rule in rules)
        {
            var ours = string.Equals(rule.Name, RuleName, StringComparison.OrdinalIgnoreCase)
                || (executable is not null && rule.ApplicationName is not null && string.Equals(Path.GetFullPath(rule.ApplicationName), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase));
            if (!ours || rule.Direction != DirectionIn)
            {
                continue;
            }

            exists = true;
            if (!rule.Enabled || rule.Protocol is not (ProtocolTcp or ProtocolAny) || !PortMatches(rule.LocalPorts, port))
            {
                continue;
            }

            if (rule.Action == ActionAllow) allowed = true;
            if (rule.Action == ActionBlock) blocked = true;
        }

        return new FirewallStatus(exists, allowed && !blocked, blocked, port);
    }

    /// <summary><c>*</c> or empty = any port; otherwise a comma list of numbers and ranges.</summary>
    public static bool PortMatches(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts) || localPorts.Trim() == "*")
        {
            return true;
        }

        foreach (var part in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (range.Length == 2 && int.TryParse(range[0], out var low) && int.TryParse(range[1], out var high) && port >= low && port <= high)
            {
                return true;
            }

            if (range.Length == 1 && int.TryParse(range[0], out var single) && single == port)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Replaces the FxDeck rules through an elevated netsh (UAC prompt on this PC, never in the browser).</summary>
    public async Task<FirewallAllowResult> AllowAsync(int port, CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), BuildAllowCommandLine(port))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Firewall rule {Rule} added for port {Port}", RuleName, port);
                return new FirewallAllowResult(FirewallAllowOutcome.Added, null);
            }

            _logger.LogWarning("netsh exited with {Code}", process.ExitCode);
            return new FirewallAllowResult(FirewallAllowOutcome.Failed, $"netsh exited with code {process.ExitCode}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new FirewallAllowResult(FirewallAllowOutcome.Cancelled, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning("Could not start netsh: {Message}", ex.Message);
            return new FirewallAllowResult(FirewallAllowOutcome.Failed, ex.Message);
        }
    }

    private static List<Rule> ReadRules()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new NotSupportedException("Windows Firewall COM API is not available.");
        dynamic policy = Activator.CreateInstance(type)!;
        var list = new List<Rule>();
        foreach (var item in (IEnumerable)policy.Rules)
        {
            dynamic rule = item;
            list.Add(new Rule(
                (string)rule.Name,
                (string?)rule.ApplicationName,
                (bool)rule.Enabled,
                (int)rule.Direction,
                (int)rule.Action,
                (int)rule.Protocol,
                (string?)rule.LocalPorts));
        }

        return list;
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 1-65535.");
        }
    }
}

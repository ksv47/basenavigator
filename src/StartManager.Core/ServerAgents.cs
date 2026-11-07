using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace StartManager.Core;

/// <summary>A 1C:Enterprise server agent installed as a Windows service of this computer: ragent.exe of its ImagePath, with the
/// bitness of that file. Running is null when the state of the service could not be read.</summary>
public sealed record ServerAgent(string Service, string Executable, Version Version, PlatformArchitecture Architecture, bool? Running);

/// <summary>«Перед запуском ИБ проверять версию Агента сервера» (CheckRAgent of the original, _Unit207.pas:39190–39339): before a
/// server base starts, the version of the client is compared with the server agent of this computer. As in the original, the agent
/// is looked for on this computer only (the server of Srvr= is not compared); none found — no check. Windows only.</summary>
public static class ServerAgents
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>The agents of the services of this computer; a service that cannot be read is skipped.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<ServerAgent> Find()
    {
        var result = new List<ServerAgent>();
        RegistryKey? services = null;
        string[] names;
        // The services cannot be listed (no access): no agent is known and the optional check is skipped (Codex audit 14.10).
        try { services = Registry.LocalMachine.OpenSubKey(ServicesKey); names = services?.GetSubKeyNames() ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { services?.Dispose(); return result; }
        using var _ = services;
        foreach (var name in names)
        {
            try
            {
                using var service = services!.OpenSubKey(name);
                // Expanded before it is parsed: the variable may hold the whole path (Codex audit 14.10).
                var image = service?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (Executable(image is null ? null : Environment.ExpandEnvironmentVariables(image)) is not { } path) continue;
                if (PlatformCatalog.Read(path) is not { } installation) continue;
                result.Add(new(name, installation.Executable, installation.Version, installation.Architecture, IsRunning(name)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        }
        return result;
    }

    /// <summary>The ragent.exe of a service command line: quoted or not, with or without the arguments after it; null for another program.</summary>
    public static string? Executable(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var text = imagePath.Trim();
        string path;
        if (text.StartsWith('"')) { var end = text.IndexOf('"', 1); if (end < 0) return null; path = text[1..end]; }
        else
        {
            // Unquoted: the path ends with the whole name ragent.exe (it may contain spaces), then the end or the arguments;
            // «ragent.exe.bak» is another file (Codex audit 14.10).
            var at = -1;
            for (var from = 0; (from = text.IndexOf("ragent.exe", from, StringComparison.OrdinalIgnoreCase)) >= 0; from++)
            {
                var end = from + "ragent.exe".Length;
                if (end == text.Length || char.IsWhiteSpace(text[end])) { at = end; break; }
            }
            if (at < 0) return null;
            path = text[..at];
        }
        // A Windows path on any OS: Path.GetFileName of Linux does not split at «\» (Codex re-check 14.10).
        return path[(path.LastIndexOfAny(['\\', '/']) + 1)..].Equals("ragent.exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    /// <summary>The version of the server agent when it differs from the client's; null when it is the same or no agent is found.
    /// The agents are taken by state, the first group that is not empty: the running ones, then those whose state could not be read
    /// (possibly running), then the stopped ones; a running mismatch is not hidden by an agent of unknown state (Codex audit, Grok
    /// audit 14.10). Within the group, as the original keeps the x32 and x64 agents apart and compares a client with the agents of
    /// its own bitness (GetServerAgent(Bit64), _Unit203.pas:1061–1062, _Unit207.pas:39240–39243): when the client is x86 or x64
    /// and an agent of the same bitness is there, only those count (Grok audit 14.10). Then any of the client's version is enough,
    /// else the highest; the original took the last one found, here the choice does not depend on the order.</summary>
    public static Version? Mismatch(Version client, PlatformArchitecture clientArchitecture, IEnumerable<ServerAgent> agents)
    {
        var all = agents.ToList();
        var candidates = new[] { (bool?)true, null, false }.Select(state => all.Where(x => x.Running == state).ToList()).FirstOrDefault(x => x.Count > 0);
        if (candidates is null) return null;
        if (clientArchitecture is PlatformArchitecture.X86 or PlatformArchitecture.X64 && candidates.Any(x => x.Architecture == clientArchitecture))
            candidates = candidates.Where(x => x.Architecture == clientArchitecture).ToList();
        return candidates.Any(x => x.Version == client) ? null : candidates.Max(x => x.Version);
    }

    [SupportedOSPlatform("windows")]
    private static bool? IsRunning(string service)
    {
        var manager = OpenSCManagerW(null, null, 0x0001); // SC_MANAGER_CONNECT
        if (manager == IntPtr.Zero) return null;
        try
        {
            var handle = OpenServiceW(manager, service, 0x0004); // SERVICE_QUERY_STATUS
            if (handle == IntPtr.Zero) return null;
            try { return QueryServiceStatus(handle, out var status) ? status.CurrentState == 4 : null; } // SERVICE_RUNNING
            finally { CloseServiceHandle(handle); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string service, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

using System.IO;

namespace Isafom.KxSentinel;

internal sealed record PortDescriptor(string Name, string Monitor, uint Type);
internal sealed record MonitorDescriptor(string Name, string Dll);
internal sealed record FilePortReport(bool ReadOnly, bool PortEnumerationSucceeded, bool MonitorEnumerationSucceeded,
    bool? FilePortPresent, bool? FilePortUnique, bool? LocalPortTypeVerified,
    bool? MonitorResolvedUniquely, bool? WindowsLocalMonitorRecognized,
    bool? BuiltinMonitorAliasUsed, bool SafeFilePortAvailable, string Reason, int? Win32Error = null);

internal static class FilePortPolicy
{
    internal static FilePortReport Unavailable(string reason, int? error = null) =>
        new(true, false, false, null, null, null, null, null, null, false, reason, error);

    internal static FilePortReport Evaluate(PortDescriptor[] ports, MonitorDescriptor[] monitors)
    {
        var candidates = ports.Where(p => p.Name.Equals("FILE:", StringComparison.OrdinalIgnoreCase)).ToArray();
        var result = new FilePortReport(true, true, true, candidates.Length > 0, candidates.Length == 1,
            null, null, null, null, false, "FilePortNotPresent");
        if (candidates.Length == 0) return result;
        if (candidates.Length != 1) return result with { Reason = "FilePortAmbiguous" };
        var port = candidates[0];
        bool localType = (port.Type & 1) != 0 && (port.Type & (4u | 8u)) == 0;
        result = result with { LocalPortTypeVerified = localType };
        if (!localType) return result with { Reason = "FilePortNotLocal" };
        var matching = monitors.Where(m => m.Name.Equals(port.Monitor, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool alias = false;
        if (matching.Length == 0 && port.Monitor.Equals("Local Monitor", StringComparison.OrdinalIgnoreCase))
        {
            // Observed on real Windows: PORT_INFO_2 says "Local Monitor", while
            // MONITOR_INFO_2 registers the same built-in component as "Local Port".
            // Resolve only this exact alias, and still require localspl.dll.
            matching = monitors.Where(m => m.Name.Equals("Local Port", StringComparison.OrdinalIgnoreCase)).ToArray();
            alias = true;
        }
        result = result with { MonitorResolvedUniquely = matching.Length == 1, BuiltinMonitorAliasUsed = alias };
        if (matching.Length != 1) return result with { Reason = "FileMonitorNotResolved" };
        bool windowsLocal = LocalDll(matching[0].Dll);
        return result with { WindowsLocalMonitorRecognized = windowsLocal, SafeFilePortAvailable = windowsLocal,
            Reason = windowsLocal ? "FilePortVerified" : "FileMonitorNotWindowsLocal" };
    }

    private static bool LocalDll(string dll)
    {
        if (dll.Equals("localspl.dll", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return Path.IsPathFullyQualified(dll) && Path.GetFullPath(dll).Equals(
                Path.Combine(Environment.SystemDirectory, "localspl.dll"), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

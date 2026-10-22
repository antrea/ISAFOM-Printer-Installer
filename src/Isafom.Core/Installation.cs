namespace Isafom.Core;

public static class InputRules
{
    public static string Name(string? name)
    {
        var value = name?.Trim() ?? "";
        if (value.Length is 0 or > 31 || value.Any(char.IsControl)) throw new InstallerException(ErrorCode.InvalidName);
        return value;
    }
    public static void Account(ReadOnlySpan<char> value)
    {
        if (value.Length is < 1 or > 8) throw new InstallerException(ErrorCode.InvalidAccount);
        foreach (char c in value) if (c is < '0' or > '9') throw new InstallerException(ErrorCode.InvalidAccount);
    }
}

public sealed record PortState(string Name, string Host, int Protocol, int PortNumber, bool SnmpEnabled, string Community, int SnmpIndex);
public sealed record DriverState(string Name, string Version);
public sealed record QueueState(string Name, string DriverName, string PortName, bool Shared, int Jobs, bool Managed);
public sealed record SystemState(bool SupportedWindows, bool X64, bool SpoolerRunning,
    DriverState[] Drivers, PortState[] Ports, QueueState[] Queues);
public sealed record Endpoint(string Address, bool UsedFallback);
public enum InstallKind { Install, Configure, Repair }
public sealed record InstallationPlan(InstallKind Kind, string QueueName, string PortName, bool AddDriver, bool AddPort, bool AddQueue, bool RepairQueue);

public static class Planner
{
    public static InstallationPlan Create(PrinterSettings settings, SystemState state, Endpoint endpoint)
    {
        if (!state.SupportedWindows) throw new InstallerException(ErrorCode.UnsupportedWindows);
        if (!state.X64) throw new InstallerException(ErrorCode.UnsupportedArchitecture);
        if (!state.SpoolerRunning) throw new InstallerException(ErrorCode.SpoolerUnavailable);
        var drivers = state.Drivers.Where(d => d.Name.Equals(settings.DriverName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (drivers.Any(d => d.Version != settings.DriverVersion)) throw new InstallerException(ErrorCode.DriverConflict);
        // Locate existing queues by BOTH driver and target, never by model name alone.
        var candidates = state.Queues.Where(q => q.Name.Equals(settings.PrinterName, StringComparison.OrdinalIgnoreCase) ||
            (q.DriverName.Equals(settings.DriverName, StringComparison.OrdinalIgnoreCase) && state.Ports.Any(p => p.Name.Equals(q.PortName, StringComparison.OrdinalIgnoreCase) && IsTarget(settings, p.Host)))).ToArray();
        if (candidates.Length > 1) throw new InstallerException(ErrorCode.AmbiguousQueue);
        var queue = candidates.SingleOrDefault();
        if (queue is not null && (!queue.DriverName.Equals(settings.DriverName, StringComparison.OrdinalIgnoreCase) || queue.Shared)) throw new InstallerException(ErrorCode.QueueConflict);
        if (queue is not null && !queue.Managed && !state.Ports.Any(p => p.Name.Equals(queue.PortName, StringComparison.OrdinalIgnoreCase) && IsTarget(settings, p.Host)))
            throw new InstallerException(ErrorCode.QueueConflict);
        // Reuse only the endpoint actually probed. A working DNS socket says nothing
        // about a legacy IP port, and a failed DNS name must not be reused on fallback.
        var correctPort = state.Ports.FirstOrDefault(p => p.Host.Equals(endpoint.Address, StringComparison.OrdinalIgnoreCase) && CorrectPort(settings, p));
        var portName = correctPort?.Name ?? settings.PortName;
        if (correctPort is null && state.Ports.Any(p => p.Name.Equals(portName, StringComparison.OrdinalIgnoreCase))) throw new InstallerException(ErrorCode.PortConflict);
        bool repair = queue is not null && !queue.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase);
        if (repair && (!queue!.Managed || queue.Jobs != 0))
            throw new InstallerException(queue.Jobs != 0 ? ErrorCode.ExistingJobs : ErrorCode.QueueConflict);
        // Updating personal preferences while there are queued jobs is also deferred.
        if (queue is not null && queue.Jobs != 0) throw new InstallerException(ErrorCode.ExistingJobs);
        return new(queue is null ? InstallKind.Install : repair ? InstallKind.Repair : InstallKind.Configure,
            queue?.Name ?? settings.PrinterName, portName, drivers.Length == 0, correctPort is null, queue is null, repair);
    }
    public static bool IsTarget(PrinterSettings settings, string host) =>
        host.Equals(settings.Host, StringComparison.OrdinalIgnoreCase) || host == settings.FallbackIPv4;
    public static bool CorrectPort(PrinterSettings settings, PortState port) =>
        port.Protocol == 1 && port.PortNumber == settings.PortNumber && port.SnmpEnabled &&
        port.Community == settings.SnmpCommunity && port.SnmpIndex == settings.SnmpIndex;
}

public enum Stage { Inspecting, Network, Driver, Queue, Preferences, Verification, Finished, RemovalPlanning, Removing, RemovalFinished, RemovalCancelled }
public interface IEventSink { void Record(Stage stage, ErrorCode? error = null); }
public interface IWindowsMachine
{
    Task<SystemState> InspectAsync(CancellationToken token);
    Task<Endpoint> ResolveAndProbeAsync(PrinterSettings settings, CancellationToken token);
    Task EnsureAsync(PrinterSettings settings, Endpoint endpoint, InstallationPlan plan, CancellationToken token);
}

public sealed record PreferenceSupport(bool Available, bool ValidatedForExactDriver, bool OriginalUserContextPreserved);
public sealed record PreferenceChecks(bool UserNameMatches, bool JobAccountingEnabled, bool AccountMatches,
    bool JobAccountingInPreferences, bool PrivatePrint, bool PasswordPromptDisabled, bool PrivatePasswordMatches,
    bool Monochrome, bool PersistedAfterReopen)
{
    public bool All => UserNameMatches && JobAccountingEnabled && AccountMatches && JobAccountingInPreferences &&
        PrivatePrint && PasswordPromptDisabled && PrivatePasswordMatches && Monochrome && PersistedAfterReopen;
}

// Native adapters must be bound to a verified component and independent readback.
// No guessed private DEVMODE patches. Secrets may use private IPC, never files or argv.
public interface IKxPreferences
{
    void ValidateInputs(string name, ReadOnlySpan<char> account) { }
    PreferenceSupport Support(PrinterSettings settings);
    Task ApplyForCurrentUserAsync(string queue, string userName, ReadOnlyMemory<char> account, CancellationToken token);
    Task<PreferenceChecks> VerifyForCurrentUserAsync(string queue, string userName, ReadOnlyMemory<char> account, CancellationToken token);
    Task CompleteForCurrentUserAsync(string queue, string userName, ReadOnlyMemory<char> account, CancellationToken token) => Task.CompletedTask;
}

public sealed class UnavailableKxPreferences : IKxPreferences
{
    public PreferenceSupport Support(PrinterSettings settings) => new(false, false, false);
    public Task ApplyForCurrentUserAsync(string queue, string userName, ReadOnlyMemory<char> account, CancellationToken token) =>
        throw new InstallerException(ErrorCode.ProviderUnavailable);
    public Task<PreferenceChecks> VerifyForCurrentUserAsync(string queue, string userName, ReadOnlyMemory<char> account, CancellationToken token) =>
        throw new InstallerException(ErrorCode.ProviderUnavailable);
}

public sealed record InstallationResult(string QueueName, InstallKind Kind, bool Fallback, PreferenceChecks Checks)
{
    // All locally observable checks are required before returning success.
    // Physical release/panel/reboot tests are tracked separately in VALIDATION.md, never inferred here.
    public bool ConfigurationVerified => Checks.All;
}

public sealed class InstallerEngine(IWindowsMachine machine, IKxPreferences preferences, IEventSink events)
{
    public async Task<InstallationResult> InstallAsync(PrinterSettings settings, string rawName, ReadOnlyMemory<char> account,
        IProgress<Stage>? progress = null, CancellationToken token = default)
    {
        void Report(Stage stage) { events.Record(stage); progress?.Report(stage); }
        var name = InputRules.Name(rawName);
        InputRules.Account(account.Span);
        preferences.ValidateInputs(name, account.Span);
        settings.Validate();
        Report(Stage.Inspecting);
        var state = await machine.InspectAsync(token);
        if (!state.SupportedWindows) throw new InstallerException(ErrorCode.UnsupportedWindows);
        if (!state.X64) throw new InstallerException(ErrorCode.UnsupportedArchitecture);
        if (!state.SpoolerRunning) throw new InstallerException(ErrorCode.SpoolerUnavailable);
        var support = preferences.Support(settings);
        if (!support.Available) throw new InstallerException(ErrorCode.ProviderUnavailable);
        if (!support.ValidatedForExactDriver || !support.OriginalUserContextPreserved) throw new InstallerException(ErrorCode.ProviderNotValidated);
        Report(Stage.Network);
        var endpoint = await machine.ResolveAndProbeAsync(settings, token);
        Report(Stage.Driver);
        var plan = Planner.Create(settings, state, endpoint);
        token.ThrowIfCancellationRequested();
        await machine.EnsureAsync(settings, endpoint, plan, token);
        Report(Stage.Preferences);
        await preferences.ApplyForCurrentUserAsync(plan.QueueName, name, account, token);
        Report(Stage.Verification);
        var finalState = await machine.InspectAsync(token);
        var finalPlan = Planner.Create(settings, finalState, endpoint);
        if (finalPlan.AddDriver || finalPlan.AddPort || finalPlan.AddQueue || finalPlan.RepairQueue)
            throw new InstallerException(ErrorCode.Verification);
        var checks = await preferences.VerifyForCurrentUserAsync(plan.QueueName, name, account, token);
        if (!checks.All) throw new InstallerException(ErrorCode.Verification);
        await preferences.CompleteForCurrentUserAsync(plan.QueueName, name, account, token);
        Report(Stage.Finished);
        return new(plan.QueueName, plan.Kind, endpoint.UsedFallback, checks);
    }
}

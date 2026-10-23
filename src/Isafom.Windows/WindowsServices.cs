using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Isafom.Core;

namespace Isafom.Windows;

public static class AdminConfiguration
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CNR-ISAFOM");
    public static PrinterSettings Load()
    {
        var file = Path.Combine(DirectoryPath, "printer-settings.json");
        if (!File.Exists(file)) return PrinterSettings.Default();
        EnsureProtected(DirectoryPath);
        var info = new FileInfo(file);
        if (info.Length > 16384 || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InstallerException(ErrorCode.Configuration);
        EnsureProtected(file);
        return PrinterSettings.Parse(File.ReadAllText(file));
    }

    public static void EnsureProtected(string path)
    {
        // Refuse reparse points anywhere in the existing path and writable configuration/staging directories.
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InstallerException(ErrorCode.Configuration);
        FileSystemSecurity acl = Directory.Exists(full) ? new DirectoryInfo(full).GetAccessControl() : new FileInfo(full).GetAccessControl();
        var trusted = new[] { "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" };
        var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ?? throw new InstallerException(ErrorCode.Configuration);
        if (!trusted.Contains(owner.Value)) throw new InstallerException(ErrorCode.Configuration);
        const FileSystemRights writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 &&
                !trusted.Contains(((SecurityIdentifier)rule.IdentityReference).Value))
                throw new InstallerException(ErrorCode.Configuration);
    }
}

public sealed class SafeEventLog : IEventSink
{
    public event Action<string>? Appended;
    public event Action<Stage, ErrorCode?>? Reported;
    public string PathName { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CNR-ISAFOM", "installer.log");
    public void Record(Stage stage, ErrorCode? error = null)
    {
        // Only enums and UTC timestamps can reach this log. No caller-provided text is accepted.
        string line = $"{DateTimeOffset.UtcNow:O} stage={stage} code={error?.ToString() ?? "OK"}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            if (File.Exists(PathName) && new FileInfo(PathName).Length > 256 * 1024) File.Delete(PathName);
            File.AppendAllText(PathName, line + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        // A disk logging failure must not prevent live progress in the UI.
        Appended?.Invoke(line);
        Reported?.Invoke(stage, error);
    }
}

public static class PowerShellBackend
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    public static async Task<T> RunAsync<T>(object input, CancellationToken token)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Isafom.Windows.Scripts.PrinterBackend.ps1")!;
        using var reader = new StreamReader(source);
        var code = await reader.ReadToEndAsync(token);
        using var removalSource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Isafom.Windows.Scripts.RemovalBackend.ps1")!;
        using var removalReader = new StreamReader(removalSource);
        code = await removalReader.ReadToEndAsync(token) + Environment.NewLine + code;
        using var selectiveSource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Isafom.Windows.Scripts.SelectiveRemoval.ps1")!;
        using var selectiveReader = new StreamReader(selectiveSource);
        code = await selectiveReader.ReadToEndAsync(token) + Environment.NewLine + code;
        using var knownSource=Assembly.GetExecutingAssembly().GetManifestResourceStream("Isafom.Windows.Driver.KnownSpoolFiles.json")!;
        using var knownReader=new StreamReader(knownSource);
        string knownJson=await knownReader.ReadToEndAsync(token);
        code="$script:IsafomKnownDriverFiles = '"+knownJson.Replace("'","''")+"' | ConvertFrom-Json\n"+code;
        using var locksSource=Assembly.GetExecutingAssembly().GetManifestResourceStream("Isafom.Windows.Scripts.DriverLocks.cs.txt")!;
        using var locksReader=new StreamReader(locksSource);
        string locksCode=await locksReader.ReadToEndAsync(token);
        code="$script:IsafomDriverLocksSource = '"+locksCode.Replace("'","''")+"'\n"+code;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        // Keep the command line short regardless of embedded script size. Both
        // constant code and the non-personal request travel over redirected stdin.
        const string bootstrap = "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); $envelope=[Console]::In.ReadToEnd()|ConvertFrom-Json; & ([ScriptBlock]::Create([string]$envelope.Code)) ([string]$envelope.Request)";
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InstallerException(ErrorCode.MachineFailure);
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token); // drain; never log raw PowerShell errors
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Code = code, Request = JsonSerializer.Serialize(input) }));
        process.StandardInput.Close();
        // Do not kill a spooler operation partway through a mutation.
        await process.WaitForExitAsync(CancellationToken.None);
        var output = await outputTask;
        _ = await errorTask;
        if (output.Length > 4 * 1024 * 1024) throw new InstallerException(ErrorCode.Protocol);
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                var failure = JsonSerializer.Deserialize<MachineReply>(output, Options);
                if (typeof(T) == typeof(MachineReply) && failure is not null) return (T)(object)failure;
                throw new InstallerException(failure?.Code ?? ErrorCode.MachineFailure);
            }
            if (process.ExitCode != 0) throw new InstallerException(ErrorCode.MachineFailure);
            return JsonSerializer.Deserialize<T>(output, Options) ?? throw new InstallerException(ErrorCode.Protocol);
        }
        catch (JsonException) { throw new InstallerException(ErrorCode.MachineFailure); }
    }
}

public sealed class WindowsMachine(PrinterSettings settings) : IWindowsMachine
{
    public Task<SystemState> InspectAsync(CancellationToken token) => PowerShellBackend.RunAsync<SystemState>(new { Mode = "Inspect", Settings = settings }, token);
    public async Task<Endpoint> ResolveAndProbeAsync(PrinterSettings s, CancellationToken token)
    {
        IPAddress[] addresses;
        bool fallback = false;
        try
        {
            using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            dnsTimeout.CancelAfter(TimeSpan.FromSeconds(6));
            addresses = await Dns.GetHostAddressesAsync(s.Host, dnsTimeout.Token);
            if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        }
        catch (SocketException e) when (s.AllowFallbackOnDnsFailure && e.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
        {
            addresses = [IPAddress.Parse(s.FallbackIPv4)];
            fallback = true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InstallerException(ErrorCode.DnsTimeout); }
        catch (SocketException) { throw new InstallerException(ErrorCode.Network); }
        foreach (var address in addresses)
        {
            using var tcp = new TcpClient(address.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                // Connect and close only: never send bytes to RAW 9100.
                await tcp.ConnectAsync(address, s.PortNumber, timeout.Token);
                return new(fallback ? s.FallbackIPv4 : s.Host, fallback);
            }
            catch (Exception e) when (e is SocketException || (e is OperationCanceledException && !token.IsCancellationRequested)) { }
        }
        throw new InstallerException(ErrorCode.Network);
    }
    public Task EnsureAsync(PrinterSettings s, Endpoint endpoint, InstallationPlan plan, CancellationToken token)
    {
        bool pending=false;
        if(!plan.AddQueue)
        {
            using var printer=Isafom.KxSentinel.PrinterStore.Open(plan.QueueName);
            pending=Isafom.KxSentinel.PrinterStore.Info(printer).Comment == Kx85Preferences.UserMarker(WindowsIdentity.GetCurrent().User!.Value)+" pending";
            if(pending)
            {
                try { using var secure=Isafom.KxSentinel.PrinterStore.Open(plan.QueueName,0x20008); Kx85Preferences.RequireOwned(s,plan.QueueName,secure); pending=false; }
                catch(InstallerException) { }
            }
        }
        return plan.AddDriver || plan.AddPort || plan.AddQueue || plan.RepairQueue || pending
            ? MachineElevation.RunAsync(s, endpoint, token) : Task.CompletedTask;
    }
}

public enum MachineStep { None, Elevation, Handshake, Inspect, Driver, Port, Queue, Pause, Complete }
public sealed record MachineReply(bool Ok, ErrorCode? Code, MachineStep Step = MachineStep.None, string? NativeCode = null, RemovalResult? Removal = null, SelectiveQueueResult[]? SelectiveRemoval = null);
public sealed record MachineRequest(string ConfigFingerprint, string Endpoint, string OwnerSid, string Operation = "Ensure", string? PlanFingerprint = null, bool RemoveDriver = false, string? DriverVersion = null,bool AllowSpoolerRestart=false,bool ForceRemoval=false, string[]? QueueIds=null);

public static class MachineElevation
{
    internal static string? ExecutableOverride;
    public static MachineStep LastStep { get; private set; }
    public static string? LastNativeCode { get; private set; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken token)
    {
        // Bounded protocol. No StreamReader buffering of arbitrarily large messages.
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 4 * 1024 * 1024)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new InstallerException(ErrorCode.Protocol);
            if (one[0] == 10) return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
        throw new InstallerException(ErrorCode.Protocol);
    }
    private static async Task WriteAsync(Stream stream, object value, CancellationToken token)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n"), token);
        await stream.FlushAsync(token);
    }

    public static async Task RunAsync(PrinterSettings settings, Endpoint endpoint, CancellationToken token)
    { await RunOperationAsync(settings, endpoint.Address, "Ensure", null, false, token); }

    internal static async Task<RemovalResult> RemoveAsync(PrinterSettings settings, string fingerprint, bool removeDriver,bool allowSpoolerRestart=false,bool forceRemoval=false)
        => (await RunOperationAsync(settings, "", "Remove", fingerprint, removeDriver, CancellationToken.None,allowSpoolerRestart,forceRemoval)).Removal
            ?? throw new InstallerException(ErrorCode.Protocol);

    internal static async Task<SelectiveQueueResult[]> RemoveSelectedAsync(PrinterSettings settings, string fingerprint, string[] queueIds)
        => (await RunOperationAsync(settings, "", "RemoveSelected", fingerprint, false, CancellationToken.None, queueIds:queueIds)).SelectiveRemoval
            ?? throw new InstallerException(ErrorCode.Protocol);

    private static async Task<MachineReply> RunOperationAsync(PrinterSettings settings, string endpoint, string operation, string? fingerprint, bool removeDriver, CancellationToken token,bool allowSpoolerRestart=false,bool forceRemoval=false,string[]? queueIds=null)
    {
        string pipeName = "ISAFOM-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        LastStep = MachineStep.Elevation;
        var start = new ProcessStartInfo(ExecutableOverride ?? Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Installa_Stampante_ISAFOM.dll"));
        foreach (var arg in new[] { "--machine-helper", pipeName, Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
        Process process;
        try { process = await Task.Run(() => Process.Start(start), token) ?? throw new InstallerException(ErrorCode.MachineFailure); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { throw new InstallerException(ErrorCode.ElevationCancelled); }
        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            LastStep = MachineStep.Handshake;
            var connection = pipe.WaitForConnectionAsync(timeout.Token);
            if (await Task.WhenAny(connection, process.WaitForExitAsync(timeout.Token)) != connection)
                throw new InstallerException(ErrorCode.MachineFailure);
            await connection;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != process.Id) throw new InstallerException(ErrorCode.Protocol);
            await WriteAsync(pipe, new MachineRequest(settings.Fingerprint(), endpoint, WindowsIdentity.GetCurrent().User!.Value, operation, fingerprint, removeDriver, settings.DriverVersion,allowSpoolerRestart,forceRemoval,queueIds), timeout.Token);
            var reply = JsonSerializer.Deserialize<MachineReply>(await ReadLineAsync(pipe, timeout.Token)) ?? throw new InstallerException(ErrorCode.Protocol);
            LastStep = reply.Step;
            LastNativeCode = reply.NativeCode is not null && System.Text.RegularExpressions.Regex.IsMatch(reply.NativeCode, "^0x[0-9A-Fa-f]{8}$") ? reply.NativeCode : null;
            if (!reply.Ok) throw new InstallerException(reply.Code ?? ErrorCode.MachineFailure);
            return reply;
        }
    }

    public static async Task<int> HelperAsync(string pipeName, int parentPid)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(pipeName, @"\AISAFOM-[a-f0-9]{32}\z")) return 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(30000, timeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != parentPid) return 2;
            var request = JsonSerializer.Deserialize<MachineRequest>(await ReadLineAsync(pipe, timeout.Token)) ?? throw new InstallerException(ErrorCode.Protocol);
            var settings = DriverProfiles.Select(AdminConfiguration.Load(), request.DriverVersion ?? DriverProfiles.DefaultVersion);
            if (request.ConfigFingerprint != settings.Fingerprint() || request.Operation is not ("Ensure" or "Remove" or "RemoveSelected")) throw new InstallerException(ErrorCode.Configuration);
            if (request.Operation == "Ensure" && !Planner.IsTarget(settings, request.Endpoint)) throw new InstallerException(ErrorCode.Configuration);
            if (request.Operation is ("Remove" or "RemoveSelected") && (request.Endpoint.Length != 0 || request.PlanFingerprint is null ||
                !System.Text.RegularExpressions.Regex.IsMatch(request.PlanFingerprint, "\\A[A-F0-9]{64}\\z"))) throw new InstallerException(ErrorCode.Protocol);
            if(request.ForceRemoval)throw new InstallerException(ErrorCode.Protocol);
            if(request.Operation == "RemoveSelected" && (request.QueueIds is not {Length: >0 and <=128} ||
                request.QueueIds.Any(id=>!Guid.TryParseExact(id,"D",out _)) || request.QueueIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=request.QueueIds.Length))
                throw new InstallerException(ErrorCode.Protocol);
            if (ProcessIdentity.UserSid(parentPid) != request.OwnerSid) throw new InstallerException(ErrorCode.Protocol);
            // Defense in depth: direct helper invocation cannot bypass the unavailable adapter gate.
            var support = new Kx85Preferences(settings).Support(settings);
            if (request.Operation == "Ensure" && (!support.Available || !support.ValidatedForExactDriver || !support.OriginalUserContextPreserved)) throw new InstallerException(ErrorCode.ProviderUnavailable);
            using var mutex = new Mutex(false, "Global\\CNR-ISAFOM-Printer-Installer-Machine");
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InstallerException(ErrorCode.Busy);
            try
            {
                if(request.Operation == "RemoveSelected")
                {
                    var selected = await PowerShellBackend.RunAsync<MachineReply>(new {Mode="RemoveSelected",Settings=settings,
                        OwnerSid=request.OwnerSid,request.PlanFingerprint,request.QueueIds},CancellationToken.None);
                    await WriteAsync(pipe,selected,timeout.Token);
                    return selected.Ok ? 0 : 1;
                }
                if(request.Operation == "Remove")
                {
                    if(!request.RemoveDriver)throw new InstallerException(ErrorCode.RemovalBlocked);
                    _ = DriverPackage.EnsureProtectedRoot();
                    RemovalStorage.Validate();
                    var removal = await PowerShellBackend.RunAsync<MachineReply>(new { Mode = "Remove", Settings = settings,
                        OwnerSid = request.OwnerSid, request.PlanFingerprint, request.RemoveDriver, request.AllowSpoolerRestart,request.ForceRemoval }, CancellationToken.None);
                    if(removal.Ok&&removal.Removal is {QueueAbsent:true,DriverRemoved:true,PackageRemoved:true,PortRemoved:true,DriverFilesRemoved:false,RestartRequired:true} pending)
                    {
                        try
                        {
                            RemovalStorage.Validate();
                            var files=await PowerShellBackend.RunAsync<DriverFileRemovalResult>(new {Mode="FinalizeRemovalFiles",Settings=settings,OwnerSid=request.OwnerSid},CancellationToken.None);
                            removal=removal with {Removal=pending with {DriverFilesRemoved=files.Complete,DriverFilesRemaining=files.Remaining,RestartRequired=pending.PackageRestartRequired||!files.Complete}};
                        }
                        catch(InstallerException) { /* Preserve the explicit partial result; never claim success. */ }
                    }
                    if(removal.Ok&&removal.Removal is {GlobalComplete:true} completed)
                    {
                        bool storageRemoved;
                        try{storageRemoved=RemovalStorage.Clear();}
                        catch(Exception error) when(error is IOException or UnauthorizedAccessException or InstallerException){storageRemoved=false;}
                        removal=removal with {Removal=completed with {StorageRemoved=storageRemoved}};
                    }
                    await WriteAsync(pipe, removal, timeout.Token);
                    return removal.Ok ? 0 : 1;
                }
                var endpoint = await new WindowsMachine(settings).ResolveAndProbeAsync(settings, timeout.Token);
                if (request.Endpoint != endpoint.Address) throw new InstallerException(ErrorCode.Network);
                var snapshot = await new WindowsMachine(settings).InspectAsync(timeout.Token);
                string? inf = snapshot.Drivers.Any(d => d.Name == settings.DriverName && d.Version == settings.DriverVersion)
                    ? null : DriverPackage.TryInstalledInf(settings) ?? await DriverPackage.PrepareAsync(settings, timeout.Token);
                var reply = await PowerShellBackend.RunAsync<MachineReply>(new { Mode = "Ensure", Settings = settings, Endpoint = endpoint.Address, InfPath = inf, OwnerSid = request.OwnerSid }, CancellationToken.None);
                await WriteAsync(pipe, reply, timeout.Token);
                return reply.Ok ? 0 : 1;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception e)
        {
            try { await WriteAsync(pipe, new MachineReply(false, e is InstallerException known ? known.Code : ErrorCode.MachineFailure), timeout.Token); }
            catch (Exception) { /* Parent may have closed its endpoint. No raw error is persisted. */ }
            return 1;
        }
    }
}

public static class DriverPackage
{
    public static string? TryInstalledInf(PrinterSettings settings)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "DriverStore", "FileRepository");
        if (!Directory.Exists(root)) return null;
        var matches = Directory.EnumerateDirectories(root, "oemsetup.inf_amd64_*").Select(d => Path.Combine(d, "OEMSETUP.INF"))
            .Where(File.Exists).Where(f => {
                using var stream = File.OpenRead(f);
                return Convert.ToHexString(SHA256.HashData(stream)) == DriverProfiles.InfSha256(settings.DriverVersion);
            }).ToArray();
        return DriverProfiles.Supported(settings.DriverVersion) && matches.Length == 1 ? matches[0] : null;
    }
    internal static string EnsureProtectedRoot()
    {
        var root = AdminConfiguration.DirectoryPath;
        if (!Directory.Exists(root))
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.SetOwner(admin);
            foreach (var sid in new[] { admin, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(acl);
        }
        AdminConfiguration.EnsureProtected(root);
        return root;
    }
    public static async Task<string> PrepareAsync(PrinterSettings settings, CancellationToken token)
    {
        var root=EnsureProtectedRoot();
        var stage = Path.Combine(root, "driver-cache", settings.DriverVersion);
        Directory.CreateDirectory(stage);
        AdminConfiguration.EnsureProtected(stage);
        var archive = Path.Combine(stage, "driver.zip");
        bool archiveValid = false;
        if (File.Exists(archive))
        {
            AdminConfiguration.EnsureProtected(archive);
            using var existing = File.OpenRead(archive);
            archiveValid = Convert.ToHexString(SHA256.HashData(existing)) == settings.DriverArchiveSha256.ToUpperInvariant();
            if (!archiveValid) throw new InstallerException(ErrorCode.DriverIntegrity);
        }
        if (!archiveValid)
        {
            string download = archive + ".download";
            if (File.Exists(download)) AdminConfiguration.EnsureProtected(download);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            using var response = settings.DriverUri.Length == 0 ? null : await client.GetAsync(settings.DriverUri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response is not null && (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 256L * 1024 * 1024)) throw new InstallerException(ErrorCode.DriverIntegrity);
            await using (var output = new FileStream(download, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var input = response is null ? OpenEmbedded(settings) : await response.Content.ReadAsStreamAsync(token))
            {
                var buffer = new byte[65536];
                long total = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += n;
                    if (total > 256L * 1024 * 1024) throw new InstallerException(ErrorCode.DriverIntegrity);
                    await output.WriteAsync(buffer.AsMemory(0, n), token);
                }
            }
            using (var file = File.OpenRead(download))
                if (Convert.ToHexString(SHA256.HashData(file)) != settings.DriverArchiveSha256.ToUpperInvariant()) throw new InstallerException(ErrorCode.DriverIntegrity);
            File.Move(download, archive);
        }
        await using (var file = File.OpenRead(archive))
            if (Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != settings.DriverArchiveSha256.ToUpperInvariant()) throw new InstallerException(ErrorCode.DriverIntegrity);
        using (var zip = ZipFile.OpenRead(archive))
        {
            long total = 0;
            if (zip.Entries.Count > 20000) throw new InstallerException(ErrorCode.DriverIntegrity);
            string fullRoot = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries)
            {
                string prefix = settings.InfRelativePath[..(settings.InfRelativePath.LastIndexOf('/') + 1)];
                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                total += entry.Length;
                var target = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                if (total > 128L * 1024 * 1024 || !target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                    throw new InstallerException(ErrorCode.DriverIntegrity);
                if (entry.Name.Length == 0) Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target)) AdminConfiguration.EnsureProtected(target);
                    entry.ExtractToFile(target, true);
                }
            }
        }
        var infPath = Path.GetFullPath(Path.Combine(stage, settings.InfRelativePath));
        if (!File.Exists(infPath)) throw new InstallerException(ErrorCode.DriverIntegrity);
        var infText = await File.ReadAllTextAsync(infPath, token);
        var match = System.Text.RegularExpressions.Regex.Match(infText, @"(?im)^\s*DriverVer\s*=\s*[^,\r\n]+,\s*(\d+\.\d+\.\d+\.\d+)\s*$");
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version) || version != Version.Parse(settings.DriverVersion) ||
            !infText.Contains('"' + settings.DriverName + '"', StringComparison.Ordinal) ||
            !infText.Contains("NTamd64", StringComparison.OrdinalIgnoreCase)) throw new InstallerException(ErrorCode.DriverIntegrity);
        return infPath;
    }
    internal static Stream OpenEmbedded(PrinterSettings settings) => Assembly.GetExecutingAssembly().GetManifestResourceStream(settings.DriverVersion switch {
        DriverProfiles.DefaultVersion=>"Isafom.Windows.Driver.Kx85X64.zip", DriverProfiles.OptionalVersion=>"Isafom.Windows.Driver.Kx87X64.zip",
        _=>throw new InstallerException(ErrorCode.DriverIntegrity) })
        ?? throw new InstallerException(ErrorCode.DriverIntegrity);
}

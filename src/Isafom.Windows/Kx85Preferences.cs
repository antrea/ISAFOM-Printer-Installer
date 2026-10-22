using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Isafom.ConfigExtractor;
using Isafom.Core;
using Isafom.KxSentinel;

namespace Isafom.Windows;

public sealed class Kx85Preferences(PrinterSettings settings) : IKxPreferences
{
    internal static string? ExecutableOverride;
    internal static WorkerResult? LastNativeResult;
    internal static PreferenceChecks? LastChecks;
    internal const string Marker = "CNR-ISAFOM Printer Installer v2";
    internal const string AuthorComment = "Autore: Andrea Esposito - andrea.esposito@cnr.it";
    internal const string OwnerValueName = "CNR_ISAFOM_InstallerOwnerV2";
    internal static string UserMarker(string sid) => Marker + " " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
    internal static bool RecognizedIdentity(string comment, string? storedOwner, string marker) =>
        comment == marker || comment == marker + " pending" ||
        (comment == AuthorComment && storedOwner == marker);
    public PreferenceSupport Support(PrinterSettings s) => new(
        s.DriverName == "Kyocera TASKalfa 3554ci KX" && DriverProfiles.Supported(s.DriverVersion),
        s.DriverName == "Kyocera TASKalfa 3554ci KX" && DriverProfiles.Supported(s.DriverVersion), true);

    public void ValidateInputs(string name, ReadOnlySpan<char> account)
    {
        _ = InputRules.Name(name);
        InputRules.Account(account);
        if (account.Length != 4) throw new InstallerException(ErrorCode.FourDigitAccountRequired);
    }

    internal static NativeTarget Authorize(PrinterSettings s, WorkerRequest input)
    {
        new Kx85Preferences(s).ValidateInputs(input.UserName, input.AccountId);
        if (input.Token != s.PrinterName) throw new InstallerException(ErrorCode.QueueConflict);
        return new(input.Token, p => RequireOwned(s, input.Token, p));
    }

    internal static void RequireOwned(PrinterSettings s, string queue, PrinterStore.SafePrinter printer)
    {
        var info = PrinterStore.Info(printer);
        string marker = UserMarker(WindowsIdentity.GetCurrent().User!.Value);
        string? storedOwner = info.Comment == AuthorComment ? PrinterStore.StringValue(printer, OwnerValueName) : null;
        if (queue != s.PrinterName || info.Name != queue || info.Driver != s.DriverName || !RecognizedIdentity(info.Comment, storedOwner, marker) ||
            info.Jobs != 0 || (info.Attributes & (0x10u | 0x1000u)) != 0 || info.Port.IndexOfAny(['\\','/']) >= 0)
            throw new InstallerException(ErrorCode.QueueConflict);
        using var port = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports\" + info.Port, false);
        bool endpoint = port is not null && new[] { port.GetValue("HostName"), port.GetValue("IPAddress") }.OfType<string>().Any(h => Planner.IsTarget(s, h));
        if (!endpoint || port?.GetValue("Protocol") is not int protocol || protocol != 1 ||
            port.GetValue("PortNumber") is not int number || number != s.PortNumber ||
            port.GetValue("SNMP Enabled") is not int enabled || enabled != 1 ||
            port.GetValue("SNMP Community") is not string community || community != s.SnmpCommunity ||
            port.GetValue("SNMP Index") is not int index || index != s.SnmpIndex)
            throw new InstallerException(ErrorCode.PortConflict);
        // Global vendor fields belong only to this user's dedicated queue. Do
        // not update a pre-existing printer belonging to another Windows user.
        using var descriptor = PrinterStore.Get(printer, 3);
        IntPtr pointer = Marshal.ReadIntPtr(descriptor.Pointer);
        descriptor.Offset(pointer, 20);
        int length = checked((int)GetSecurityDescriptorLength(pointer));
        descriptor.Offset(pointer, length);
        byte[] bytes = new byte[length];
        try
        {
            Marshal.Copy(pointer, bytes, 0, length);
            var security = new RawSecurityDescriptor(bytes, 0);
            if (!SecurityOwned(security, WindowsIdentity.GetCurrent().User!.Value))
                throw new InstallerException(ErrorCode.QueueConflict);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        if (!Path.GetFileName(PrinterStore.Component(printer)).Equals(DriverProfiles.Component(s.DriverVersion), StringComparison.OrdinalIgnoreCase))
            throw new InstallerException(ErrorCode.DriverConflict);
    }

    internal static bool SecurityOwned(RawSecurityDescriptor security, string current)
    {
        var allowed = new[] { current, "S-1-5-18", "S-1-5-32-544" };
        if (security.DiscretionaryAcl is null || security.DiscretionaryAcl.Count == 0) return false;
        return security.DiscretionaryAcl.Cast<GenericAce>().All(a => a is CommonAce ace &&
            ace.AceQualifier == AceQualifier.AccessAllowed && allowed.Contains(ace.SecurityIdentifier.Value)) &&
            security.DiscretionaryAcl.Cast<GenericAce>().OfType<CommonAce>().Any(a => a.SecurityIdentifier.Value == current && (a.AccessMask & 0xF000C) == 0xF000C);
    }

    public async Task ApplyForCurrentUserAsync(string queue, string userName, ReadOnlyMemory<char> account, CancellationToken token)
    {
        ValidateInputs(userName, account.Span);
        var reply = await ChildAsync<WorkerResult>("--kx-apply", new(queue, userName, new string(account.Span)), token);
        if (!reply.Accepted || reply.StorageModeAfterReopen != 3 || reply.PasswordPromptAfterReopen != false || reply.ColorModeAfterReopen != 2)
            throw new InstallerException(ErrorCode.Verification);
    }

    public async Task<PreferenceChecks> VerifyForCurrentUserAsync(string queue, string userName, ReadOnlyMemory<char> account, CancellationToken token)
    {
        ValidateInputs(userName, account.Span);
        VerificationPreflight.Validate(settings, await new WindowsMachine(settings).InspectAsync(token));
        LastChecks = await ChildAsync<PreferenceChecks>("--kx-verify", new(queue, userName, new string(account.Span)), token);
        return LastChecks;
    }

    public async Task CompleteForCurrentUserAsync(string queue, string userName, ReadOnlyMemory<char> account, CancellationToken token)
    {
        var reply = await ChildAsync<FinishReply>("--kx-finish", new(queue, userName, new string(account.Span)), token);
        if (!reply.Completed) throw new InstallerException(ErrorCode.Verification);
    }
    private sealed record FinishReply(bool Completed);

    internal static PreferenceChecks ReadChecks(string queue, string name, string code, Action<PrinterStore.SafePrinter> guard)
    {
        using var printer = PrinterStore.Open(queue, 0x20008);
        guard(printer);
        var read = Kx85NativeReadback.Read(queue, name, code);
        using var server = new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
        using var target = new PrintQueue(server, queue, PrintSystemDesiredAccess.UsePrinter);
        bool accounting = true, privateMode = true, monochrome = true;
        foreach (var pair in new[] { (8u, target.DefaultPrintTicket), (9u, target.UserPrintTicket) })
        {
            using var xml = pair.Item2.GetXmlStream();
            var mode = PersonalReadbackReader.Read(xml, "Local");
            xml.Position = 0;
            var value = PersonalReadbackReader.CompareRequestedCode(xml, "Local", code);
            accounting &= mode.AccountingMode == "UseSpecificID" && value.AccountMatchesRequested == true;
            privateMode &= mode.StorageMode == "PrivatePrint";
            monochrome &= pair.Item2.OutputColor is OutputColor.Monochrome or OutputColor.Grayscale && NativeColor.Read(queue, pair.Item1) == true;
        }
        bool sameDefaults;
        using (var global = PrinterStore.Get(printer, 8))
        using (var user = PrinterStore.Get(printer, 9))
        {
            byte[] globalMode = global.DevMode(), userMode = user.DevMode();
            try { sameDefaults = globalMode.Length > 220 && globalMode.Length == userMode.Length && CryptographicOperations.FixedTimeEquals(globalMode, userMode); }
            finally { CryptographicOperations.ZeroMemory(globalMode); CryptographicOperations.ZeroMemory(userMode); }
        }
        var checks = new PreferenceChecks(read.NameMatchesUtf8 == true, read.AccountingEnabled == true,
            read.AccountMatches == true, accounting, privateMode, read.PasswordPromptDisabled == true,
            read.PrivateCodeCandidateMatches == true && sameDefaults, monochrome, false);
        return checks with { PersistedAfterReopen = checks with { PersistedAfterReopen = true } is { All: true } };
    }

    internal static int VerifyWorker(bool finish = false, PrinterSettings? settings = null)
    {
        Console.InputEncoding = new UTF8Encoding(false, true);
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var text = ReadRequest();
            using var document = JsonDocument.Parse(text);
            var fields = document.RootElement.EnumerateObject().ToArray();
            if (fields.Length != 3 || fields.Select(f => f.Name).Distinct().Count() != 3 ||
                fields.Any(f => f.Name is not ("Token" or "UserName" or "AccountId") || f.Value.ValueKind != JsonValueKind.String)) return 2;
            var request = JsonSerializer.Deserialize<WorkerRequest>(text) ?? throw new IOException();
            var target = Authorize(settings ?? AdminConfiguration.Load(), request);
            var result = ReadChecks(target.Name, request.UserName, request.AccountId, target.Require);
            if (finish)
            {
                if (!result.All) return 2;
                using var printer = PrinterStore.Open(target.Name, 0x2000c);
                string marker = UserMarker(WindowsIdentity.GetCurrent().User!.Value);
                bool completed = PrinterStore.CompletePending(printer, target, marker + " pending", marker, OwnerValueName, AuthorComment);
                Console.WriteLine(JsonSerializer.Serialize(new FinishReply(completed)));
                return completed ? 0 : 2;
            }
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0; // Nonconformity is represented by checks, not fabricated as an exception.
        }
        catch { Console.WriteLine("{}"); return 2; }
    }

    private static string ReadRequest()
    {
        char[] buffer = new char[1024]; int count = 0;
        try
        {
            for (int c; (c = Console.In.Read()) >= 0 && c != '\n';)
            { if (count == buffer.Length) throw new IOException(); buffer[count++] = (char)c; }
            return new string(buffer, 0, count);
        }
        finally { Array.Clear(buffer); }
    }

    private async Task<T> ChildAsync<T>(string action, WorkerRequest input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var printer = OpenForWorker(input.Token);
        RequireOwned(settings, input.Token, printer);
        var start = new ProcessStartInfo(ExecutableOverride ?? Environment.ProcessPath!) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Installa_Stampante_ISAFOM.dll"));
        start.ArgumentList.Add(action);
        start.ArgumentList.Add(settings.DriverVersion); // Public profile only; personal input stays on stdin.
        using var process = Process.Start(start) ?? throw new InstallerException(ErrorCode.MachineFailure);
        // Input validation and cancellation occur before starting any child.
        // Once native saving begins, only the bounded watchdog can terminate it.
        using var timeout = new CancellationTokenSource();
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(input));
        process.StandardInput.Close();
        try
        {
            // Cancellation does not terminate a mutation halfway through. The
            // bounded child is awaited; UI reports failure only afterwards.
            await process.WaitForExitAsync(timeout.Token);
            string json = await output; _ = await error;
            if (typeof(T) == typeof(WorkerResult) && json.Length <= 16384)
                LastNativeResult = JsonSerializer.Deserialize<WorkerResult>(json);
            if (process.ExitCode != 0 || json.Length > 16384) throw new InstallerException(ErrorCode.Verification);
            return JsonSerializer.Deserialize<T>(json) ?? throw new InstallerException(ErrorCode.Protocol);
        }
        catch (OperationCanceledException)
        { if (!process.HasExited) process.Kill(); throw new InstallerException(ErrorCode.Verification); }
    }

    private static PrinterStore.SafePrinter OpenForWorker(string queue)
    {
        try { return PrinterStore.Open(queue, 0x20008); }
        catch (IOException) { throw new InstallerException(ErrorCode.PrinterUnavailable); }
        catch (UnauthorizedAccessException) { throw new InstallerException(ErrorCode.PrinterUnavailable); }
    }

    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
}

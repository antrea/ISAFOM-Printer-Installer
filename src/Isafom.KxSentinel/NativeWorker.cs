using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Isafom.KxSentinel;

internal sealed record WorkerRequest(string Token, string UserName, string AccountId);
internal sealed record WorkerResult(bool Opened, bool NameAccepted, bool AccountAccepted, bool StorageAccepted, bool SaveReturnedSuccess, string Reason, string Stage = "Unknown",
    uint? StorageModeBeforeSave = null, bool? PasswordPromptBeforeSave = null,
    uint? StorageModeAfterReopen = null, bool? PasswordPromptAfterReopen = null,
    bool? DevModeValidated = null, bool? GlobalDefaultsCommitted = null, bool? UserDefaultsCommitted = null,
    bool? ColorAccepted = null, uint? ColorModeBefore = null, uint? ColorModeAfterReopen = null)
{
    internal bool Accepted => Reason == "NativeCallsCompleted" && Opened && NameAccepted && AccountAccepted && StorageAccepted && SaveReturnedSuccess &&
        DevModeValidated == true && GlobalDefaultsCommitted == true && UserDefaultsCommitted == true && ColorAccepted == true;
}

// Runs only in a short-lived child. Its stdout is a fixed boolean DTO; the
// request, DLL errors and driver strings are never forwarded to a report.
internal static class NativeWorker
{
    internal static int Run(Func<WorkerRequest, NativeTarget>? authorize = null)
    {
        Console.InputEncoding = new UTF8Encoding(false, true);
        Console.OutputEncoding = new UTF8Encoding(false);
        WorkerResult result;
        string stage = "Input";
        bool opened = false, name = false, account = false, storage = false, saved = false;
        try
        {
            using var document = JsonDocument.Parse(ReadBounded(Console.In));
            var fields = document.RootElement.EnumerateObject().ToArray();
            if (fields.Length != 3 || fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != 3 ||
                fields.Any(f => f.Name is not ("Token" or "UserName" or "AccountId") || f.Value.ValueKind != JsonValueKind.String))
                throw new IOException("InvalidWorkerRequest");
            var request = new WorkerRequest(document.RootElement.GetProperty("Token").GetString()!,
                document.RootElement.GetProperty("UserName").GetString()!, document.RootElement.GetProperty("AccountId").GetString()!);
            if (authorize is null && (!TrialPolicy.InputValid(request.UserName, request.AccountId) || request.Token.Length != 32 ||
                request.Token.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))) throw new IOException("InvalidWorkerRequest");
            var target = authorize is null
                ? new NativeTarget(TrialPolicy.QueueName(request.Token), p => PrinterStore.RequireOwned(p, request.Token))
                : authorize(request);
            using var printer = PrinterStore.Open(target.Name, authorize is null ? 12u : 0x2000cu);
            target.Require(printer);
            string component = PrinterStore.Component(printer);
            // No process-wide search path change, current-directory search,
            // driver patch, export-by-offset or downloaded binary is used.
            stage = "LoadComponent";
            IntPtr module = LoadLibraryEx(component, IntPtr.Zero, 0x100 | 0x800);
            if (module == IntPtr.Zero) throw new IOException("NativeComponentLoadFailed");
            try
            {
                var open = Export<Open>(module, "KCPDapi_OpenDeviceByName");
                var close = Export<Close>(module, "KCPDapi_CloseDevice");
                var set = Export<Set>(module, "KCPDapi_SetData");
                var save = Export<Save>(module, "KCPDapi_SaveSettingsAsDefault");
                var get = Export<Get>(module, "KCPDapi_GetData");
                target.Require(printer);
                // OpenDeviceByName calls OpenPrinterW. Its second DWORD is
                // bounded <=124 then ignored by this exact original image.
                stage = "OpenContext";
                IntPtr context = open(target.Name, 0);
                if (context == IntPtr.Zero) throw new IOException("NativeContextUnavailable");
                opened = true;
                (uint? Mode, bool? Prompt) storageBefore = (null, null);
                uint? colorBefore = ReadColor(get, context);
                bool color = false;
                try
                {
                    stage = "SetName";
                    name = Apply(set, context, 3200, Encoding.UTF8.GetBytes(request.UserName + "\0"));
                    if (name)
                    {
                        target.Require(printer);
                        stage = "SetAccount";
                        account = Apply(set, context, 3130, Encoding.ASCII.GetBytes(request.AccountId + "\0"));
                    }
                    if (account)
                    {
                        byte[] payload = new byte[72];
                        BitConverter.GetBytes(3u).CopyTo(payload, 0);
                        // byte 4 is zero: prompt disabled; UTF-16 code at 6.
                        Encoding.Unicode.GetBytes(request.AccountId.AsSpan(), payload.AsSpan(6, 66));
                        target.Require(printer);
                        stage = "SetStorage";
                        storage = Apply(set, context, 80, payload);
                        if (storage) storageBefore = ReadMode(get, context);
                    }
                    if (storage)
                    {
                        target.Require(printer);
                        stage = "SetMonochrome";
                        // Exact component SetData 50 = SetColorMode, two DWORDs.
                        // No permanent colour lock; the independently reopened
                        // PrintTicket and public DEVMODE must both confirm B/W.
                        byte[] colorPayload = new byte[8];
                        BitConverter.GetBytes(2u).CopyTo(colorPayload, 0);
                        color = Apply(set, context, 50, colorPayload);
                    }
                    if (storage && color)
                    {
                        target.Require(printer);
                        stage = "Save";
                        saved = save(context) != 0;
                    }
                    (bool Validated, bool Global, bool User) committed = (false, false, false);
                    if (saved)
                    {
                        stage = "CommitExplicitDefaults";
                        // Context+0x48 is the vendor-owned complete DEVMODE in
                        // this exact hashed component, confirmed in Initialize
                        // and SaveSettingsAsDefault. Never dereference on another
                        // version, queue or process.
                        committed = PrinterStore.CommitNativeDevMode(printer, request.Token, Marshal.ReadIntPtr(context, 0x48), target);
                    }
                    result = new(true, name, account, storage, saved, "NativeCallsCompleted", "Completed", storageBefore.Mode, storageBefore.Prompt,
                        DevModeValidated: committed.Validated, GlobalDefaultsCommitted: committed.Global, UserDefaultsCommitted: committed.User,
                        ColorAccepted: color, ColorModeBefore: colorBefore);
                }
                finally { stage = "CloseContext"; close(context); }
                if (saved)
                {
                    target.Require(printer);
                    stage = "ReopenForModeReadback";
                    IntPtr fresh = open(target.Name, 0);
                    if (fresh != IntPtr.Zero)
                    {
                        try
                        {
                            var actual = ReadMode(get, fresh);
                            result = result with { StorageModeAfterReopen = actual.Mode, PasswordPromptAfterReopen = actual.Prompt,
                                ColorModeAfterReopen = ReadColor(get, fresh) };
                        }
                        finally { close(fresh); }
                    }
                }
            }
            finally { FreeLibrary(module); }
        }
        catch { result = new(opened, name, account, storage, saved, "NativeCallsUnavailable", stage); }
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result.Accepted ? 0 : 2;
    }

    private static string ReadBounded(TextReader reader)
    {
        char[] chars = new char[1024]; int count = 0;
        try
        {
            for (int next; (next = reader.Read()) >= 0 && next != '\n';)
            {
                if (count == chars.Length) throw new IOException("InvalidWorkerRequest");
                chars[count++] = (char)next;
            }
            return new string(chars, 0, count);
        }
        finally { Array.Clear(chars); }
    }
    private static bool Apply(Set set, IntPtr context, uint id, byte[] payload)
    {
        IntPtr buffer = Marshal.AllocHGlobal(payload.Length);
        try { Marshal.Copy(payload, 0, buffer, payload.Length); return set(context, id, buffer) != 0; }
        finally
        {
            for (int i = 0; i < payload.Length; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.FreeHGlobal(buffer); CryptographicOperations.ZeroMemory(payload);
        }
    }
    private static (uint? Mode, bool? Prompt) ReadMode(Get get, IntPtr context)
    {
        IntPtr buffer = Marshal.AllocHGlobal(72);
        try
        {
            for (int i = 0; i < 72; i++) Marshal.WriteByte(buffer, i, 0);
            if (get(context, 80, buffer) == 0) return (null, null);
            uint mode = unchecked((uint)Marshal.ReadInt32(buffer));
            byte prompt = Marshal.ReadByte(buffer, 4);
            return (mode <= 10 ? mode : null, mode is 2 or 3 && prompt <= 1 ? prompt == 1 : null);
        }
        finally
        {
            for (int i = 0; i < 72; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.FreeHGlobal(buffer);
        }
    }
    private static uint? ReadColor(Get get, IntPtr context)
    {
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(buffer, 0);
            if (get(context, 50, buffer) == 0) return null;
            uint value = unchecked((uint)Marshal.ReadInt32(buffer));
            return value <= 5 ? value : null;
        }
        finally { Marshal.WriteInt64(buffer, 0); Marshal.FreeHGlobal(buffer); }
    }
    private static T Export<T>(IntPtr module, string name) where T : Delegate
    {
        IntPtr address = GetProcAddress(module, name);
        if (address == IntPtr.Zero) throw new IOException("MissingExport");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet=CharSet.Unicode)] private delegate IntPtr Open([MarshalAs(UnmanagedType.LPWStr)] string name, uint reserved);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Close(IntPtr context);
    // The native field routines return AL, not a Windows BOOL in EAX.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte Set(IntPtr context, uint id, IntPtr payload);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte Get(IntPtr context, uint id, IntPtr payload);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Save(IntPtr context);
    [DllImport("kernel32.dll", EntryPoint="LoadLibraryExW", CharSet=CharSet.Unicode)] private static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)] private static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(IntPtr module);
}

using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Isafom.Collector;

public static class StorageNamePolicy
{
    private static readonly string[] Known = ["Default DevMode", "DEVMODE", "PrinterDriverData", "UserName", "User Name", "Username", "DefaultUserName", "UseLoginName", "JobAccounting", "Job Accounting", "UseJobAccounting", "EnableJobAccounting", "Accounting", "AccountID", "Account ID", "AccountId", "UseSpecificAccountID", "PromptForAccountID", "PrivatePrint", "Private Print", "UsePrivatePrint", "JobStorage", "Job Storage", "eMPS", "Password", "PIN", "AccessCode", "PromptForPassword", "SpecifyPassword", "UserSettings", "AdminSettings", "DeviceSettings", "PrintSettings", "Settings", "Profile", "Profiles", "ColorMode", "Color", "Duplex", "InstallableOptions"];
    private static readonly HashSet<string> BooleanNames = new(["UseLoginName", "JobAccounting", "Job Accounting", "UseJobAccounting", "EnableJobAccounting", "UseSpecificAccountID", "PromptForAccountID", "PrivatePrint", "Private Print", "UsePrivatePrint", "PromptForPassword", "SpecifyPassword"], StringComparer.OrdinalIgnoreCase);
    public static string Canonical(string name) => Known.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ?? "Nome non esportato";
    public static string? SafeBoolean(string name, uint kind, ReadOnlySpan<byte> bytes) =>
        BooleanNames.Contains(name) && kind == 4 && bytes.Length == 4 && BitConverter.ToUInt32(bytes) is 0 or 1
            ? (BitConverter.ToUInt32(bytes) == 1 ? "1 (indizio, significato da validare)" : "0 (indizio, significato da validare)") : null;
}

internal sealed class SessionFingerprints : IDisposable
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    public string Compute(ReadOnlySpan<byte> bytes) => Convert.ToHexString(HMACSHA256.HashData(key, bytes));
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}

internal sealed class StorageReader(SessionFingerprints session, Dictionary<string, Fingerprint> fingerprints)
{
    private const int MaxBytes = 1024 * 1024;
    private int budget = 8 * MaxBytes;
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    private static extern int Query(SafeRegistryHandle key, string name, IntPtr reserved, out uint type, byte[]? data, ref uint size);

    public AreaEvidence[] Read(RawQueue queue, string alias)
    {
        var result = new List<AreaEvidence>();
        Add(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Print\Printers\" + queue.Name,
            "Coda globale", @"HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers\<coda>", null, 2);
        Add(RegistryHive.CurrentUser, @"Printers\DevModePerUser", "DEVMODE utente", @"HKCU\Printers\DevModePerUser\[valore della coda]", queue.Name, 0);
        Add(RegistryHive.CurrentUser, @"Printers\DevModes2", "DEVMODE utente alternativo", @"HKCU\Printers\DevModes2\[valore della coda]", queue.Name, 0);
        foreach (var vendor in new[] { "Generic", "Kyocera", "Kyocera Mita", "KYOCERA Document Solutions" })
        {
            // Candidate paths, not assertions about where KX stores its settings.
            Add(RegistryHive.CurrentUser, @"Software\" + vendor + "\\" + queue.DriverName + "\\" + queue.Name,
                "Candidato utente " + vendor + " / driver / coda", @"HKCU\Software\" + vendor + @"\<driver>\<coda>", null, 3);
            Add(RegistryHive.CurrentUser, @"Software\" + vendor + "\\" + queue.Name,
                "Candidato utente " + vendor + " / coda", @"HKCU\Software\" + vendor + @"\<coda>", null, vendor == "Kyocera Mita" ? 4 : 3);
        }
        return result.ToArray();

        void Add(RegistryHive hive, string path, string area, string safePath, string? valueOnly, int depth)
        {
            var values = new List<ValueEvidence>();
            bool truncated = false;
            string state = "Assente nel percorso esaminato; non implica funzione disabilitata";
            int keys = 0;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(path, writable: false);
                if (key is not null)
                {
                    state = "Presente, lettura completata";
                    if (valueOnly is null) Walk(key, "", 0);
                    else if (key.GetValueNames().Contains(valueOnly, StringComparer.OrdinalIgnoreCase)) ReadValue(key, valueOnly, "K01/V01", path);
                    else state = "Chiave presente, valore della coda assente";
                }
            }
            catch (UnauthorizedAccessException) { state = "Accesso negato; dati non verificabili"; }
            catch (System.Security.SecurityException) { state = "Accesso negato; dati non verificabili"; }
            catch (Exception) { state = "Lettura incompleta o non disponibile"; }
            result.Add(new(area, safePath, state, values.ToArray(), truncated));

            void Walk(RegistryKey node, string relative, int level)
            {
                if (++keys > 64) { truncated = true; return; }
                string keyAlias = $"K{keys:D2}" + (level == 0 ? "" : " (" + StorageNamePolicy.Canonical(node.Name.Split('\\')[^1]) + ")");
                var names = node.GetValueNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
                for (int i = 0; i < names.Length; i++)
                {
                    if (values.Count >= 512) { truncated = true; return; }
                    ReadValue(node, names[i], keyAlias + $"/V{i + 1:D3}", path + "\\" + relative);
                }
                var children = node.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
                if (level == depth) { if (children.Length != 0) truncated = true; return; }
                foreach (var child in children)
                {
                    if (keys >= 64 || values.Count >= 512) { truncated = true; return; }
                    using var sub = node.OpenSubKey(child, writable: false);
                    if (sub is not null) Walk(sub, relative + "\\" + child, level + 1);
                }
            }
            void ReadValue(RegistryKey node, string name, string slot, string actualPath)
            {
                uint size = 0;
                int error = Query(node.Handle, name, IntPtr.Zero, out uint type, null, ref size);
                string kind = type switch { 1 => "String", 2 => "ExpandString", 3 => "Binary", 4 => "DWord", 7 => "MultiString", 11 => "QWord", _ => "Altro" };
                string safeName = valueOnly is not null ? "Valore della coda selezionata" : name.Length == 0 ? "Valore predefinito" : StorageNamePolicy.Canonical(name);
                if (error != 0 || size > MaxBytes || size > budget)
                {
                    values.Add(new(slot, safeName, kind, size <= int.MaxValue ? (int)size : -1, "Contenuto non letto: limite o errore", null));
                    truncated = true;
                    return;
                }
                byte[] data = new byte[size];
                try
                {
                    uint actual = size;
                    if (Query(node.Handle, name, IntPtr.Zero, out type, data, ref actual) != 0 || actual > data.Length)
                    { values.Add(new(slot, safeName, kind, (int)size, "Contenuto non leggibile", null)); return; }
                    budget -= (int)actual;
                    string token = queue.Name + "\0" + hive + "\0" + actualPath + "\0" + name;
                    // Actual paths/names and keyed fingerprints stay private to this process.
                    byte[] typed = new byte[4 + actual];
                    try
                    {
                        BitConverter.TryWriteBytes(typed.AsSpan(), type);
                        data.AsSpan(0, (int)actual).CopyTo(typed.AsSpan(4));
                        fingerprints[token] = new(alias, area, slot + " — " + safeName, session.Compute(typed));
                    }
                    finally { CryptographicOperations.ZeroMemory(typed); }
                    values.Add(new(slot, safeName, kind, (int)actual, "Metadati verificati; significato proprietario non validato", StorageNamePolicy.SafeBoolean(name, type, data.AsSpan(0, (int)actual))));
                }
                finally { CryptographicOperations.ZeroMemory(data); }
            }
        }
    }
}

internal static class DevModeReader
{
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPrinter(IntPtr handle, uint level, IntPtr buffer, uint size, out uint needed);
    [DllImport("winspool.drv")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClosePrinter(IntPtr handle);

    public static DevModeEvidence Read(string queue, string alias, uint level, SessionFingerprints session, Dictionary<string, Fingerprint> fingerprints)
    {
        string scope = level == 8 ? "Globale (GetPrinter livello 8)" : "Utente corrente (GetPrinter livello 9)";
        var unavailable = new DevModeEvidence(scope, "Non verificabile", null, null, null);
        if (level is not (8 or 9)) return unavailable;
        if (!OpenPrinter(queue, out var h, IntPtr.Zero)) return unavailable;
        IntPtr buffer = IntPtr.Zero;
        uint size = 0;
        try
        {
            GetPrinter(h, level, IntPtr.Zero, 0, out size);
            if (size < IntPtr.Size || size > 1024 * 1024) return unavailable;
            buffer = Marshal.AllocHGlobal((int)size);
            if (!GetPrinter(h, level, buffer, size, out _)) return unavailable;
            IntPtr dm = Marshal.ReadIntPtr(buffer);
            if (dm == IntPtr.Zero) return new(scope, "DEVMODE assente; non implica funzione disabilitata", null, null, null);
            long offset = dm.ToInt64() - buffer.ToInt64();
            if (offset < 0 || offset + 94 > size) return unavailable;
            int publicSize = (ushort)Marshal.ReadInt16(dm, 68), extra = (ushort)Marshal.ReadInt16(dm, 70);
            if (publicSize < 94 || offset + publicSize + extra > size) return unavailable;
            bool? mono = null;
            if ((Marshal.ReadInt32(dm, 72) & 0x800) != 0) mono = Marshal.ReadInt16(dm, 92) switch { 1 => true, 2 => false, _ => null };
            var bytes = new byte[publicSize + extra];
            try
            {
                Marshal.Copy(dm, bytes, 0, bytes.Length);
                fingerprints[queue + "\0DEVMODE\0" + level] = new(alias, scope, "DEVMODE completo (contenuto omesso)", session.Compute(bytes));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return new(scope, "Struttura presente; campi privati non decodificati", publicSize, extra, mono);
        }
        catch { return unavailable; }
        finally
        {
            if (buffer != IntPtr.Zero) { for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0); Marshal.FreeHGlobal(buffer); }
            ClosePrinter(h);
        }
    }
}

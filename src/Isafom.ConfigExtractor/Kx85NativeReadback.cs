using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Isafom.KxResearch;

namespace Isafom.ConfigExtractor;

public sealed record Kx85ReversedReadback(string ToolVersion, bool ReadOnly,
    bool ExactDriverComponentVerified, bool SetupLayoutRecognized,
    bool? NameMatchesUtf8, bool? NameMatchesWindowsCodePage, bool? AccountMatches,
    bool? AccountingEnabled, bool? SpecificAccountSelected,
    bool? PrivateCodeCandidateMatches, bool PrivateRepresentationRecognized,
    bool WithinObservedFourDigitLimit, bool PerUserOverridesVerified,
    bool InstallerReady, string Scope, string Limitation, bool? PasswordPromptDisabled = null);

// Experimental, exact-version-bound readback of fields identified from native
// getters/setters. Does not load a vendor DLL, call its initialization, change
// registry/preferences, or expose the buffers through serializable objects.
internal static class Kx85NativeReadback
{
    private const int NameOffset = 0x137a;
    private const int AccountOffset = 0x16;

    internal static Kx85ReversedReadback Read(string queueName, string expectedName, string expectedAccount)
    {
        using var printer = Open(queueName);
        string component = FindComponent(printer);
        using var codec = new Kx85PrivateCodeCodec(component);
        (uint Type, byte[]? Bytes) setup = (0, null);
        (uint Type, byte[]? Bytes) privateCode = (0, null);
        byte[] utf8Name = Encoding.UTF8.GetBytes(expectedName);
        byte[]? ansiName = Ansi(expectedName);
        byte[] account = Encoding.ASCII.GetBytes(expectedAccount);
        try
        {
            setup = ReadValue(printer, "Setup");
            privateCode = ReadValue(printer, "JobId");
            bool layout = setup.Type == 3 && setup.Bytes?.Length == codec.SetupBytes;
            bool? utf8Match = layout ? Literal(setup.Bytes!, NameOffset, 0x104, utf8Name) : null;
            bool? ansiMatch = layout && ansiName is not null ? Literal(setup.Bytes!, NameOffset, 0x104, ansiName) : null;
            bool? accountMatch = layout ? Literal(setup.Bytes!, AccountOffset, 10, account) : null;
            bool? enabled = layout ? (BitConverter.ToUInt32(setup.Bytes!, 0x10) & 4) != 0 : null;
            bool? specific = layout ? setup.Bytes![0x14] == 1 && (BitConverter.ToUInt32(setup.Bytes!, 0x10) & 0x01000010) == 0 && setup.Bytes[0x15] == 0 : null;
            bool privateMatch = privateCode.Bytes is not null && privateCode.Type == 1 && CandidateMatches(codec, account, privateCode.Bytes);
            return new("0.1.3-research", true, true, layout, utf8Match, ansiMatch, accountMatch, enabled, specific,
                privateMatch ? true : null, privateMatch, expectedAccount.Length == 4, false, false,
                "Setup e JobId della sola coda selezionata; impostazioni globali del driver. Eventuali override personali non certificati.",
                "Lettura vincolata all'hash del componente originale KX 8.5 o 8.7 e alla relativa dimensione Setup. Il confronto locale del codice non verifica il rilascio fisico. Un formato diverso resta non verificabile.",
                PasswordPromptDisabled: layout ? (setup.Bytes![0xbc4] & 1) == 0 : null);
        }
        finally
        {
            if (setup.Bytes is not null) CryptographicOperations.ZeroMemory(setup.Bytes);
            if (privateCode.Bytes is not null) CryptographicOperations.ZeroMemory(privateCode.Bytes);
            CryptographicOperations.ZeroMemory(utf8Name);
            if (ansiName is not null) CryptographicOperations.ZeroMemory(ansiName);
            CryptographicOperations.ZeroMemory(account);
        }
    }

    internal static bool? Literal(byte[] data, int offset, int capacity, byte[] expected)
    {
        if (offset < 0 || capacity < 1 || offset > data.Length - capacity) return null;
        var field = data.AsSpan(offset, capacity);
        int length = field.IndexOf((byte)0);
        return length < 0 ? null : field[..length].SequenceEqual(expected);
    }

    internal static bool CandidateMatches(Kx85PrivateCodeCodec codec, byte[] requested, byte[] stored)
    {
        // Both lengths come from original native code: the modern GUI uses 33
        // bytes and the legacy PIN branch uses 5. Extra terminating NULs are
        // admitted only when every remaining byte is zero; never strip arbitrary
        // prefixes, hashes, encoding, or padding to force a match.
        foreach (int length in new[] { 5, 33 })
        {
            // On the real local Windows spooler the native legacy setter's
            // 5-byte cipher plus ANSI terminator is returned with an additional
            // UTF-16 terminator: 8 bytes total. Permit exactly this extra bound
            // for the legacy branch, still requiring every trailing byte zero.
            int maximum = length == 5 ? 8 : length + 2;
            if (requested.Length >= length || stored.Length < length || stored.Length > maximum) continue;
            if (stored.AsSpan(length).IndexOfAnyExcept((byte)0) >= 0) continue;
            byte[] encoded = codec.Encode(requested, length);
            try
            {
                if (CryptographicOperations.FixedTimeEquals(encoded, stored.AsSpan(0, length))) return true;
            }
            finally { CryptographicOperations.ZeroMemory(encoded); }
        }
        return false;
    }

    private static SafePrinter Open(string name)
    {
        var defaults = new PrinterDefaults { DesiredAccess = 8 }; // PRINTER_ACCESS_USE
        if (!OpenPrinter(name, out var printer, ref defaults)) throw new IOException("PrinterReadUnavailable");
        return printer;
    }

    private static string FindComponent(SafePrinter printer)
    {
        GetPrinterDriver(printer, null, 3, IntPtr.Zero, 0, out uint needed);
        if (needed is < 80 or > 65536) throw new IOException("DriverMetadataUnavailable");
        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetPrinterDriver(printer, null, 3, buffer, needed, out _)) throw new IOException("DriverMetadataUnavailable");
            IntPtr dependencies = Marshal.ReadIntPtr(buffer, 56); // DRIVER_INFO_3W, x64
            if (dependencies == IntPtr.Zero || dependencies.ToInt64() < buffer.ToInt64() || dependencies.ToInt64() >= buffer.ToInt64() + needed)
                throw new IOException("DriverDependencyListUnavailable");
            int remaining = checked((int)(buffer.ToInt64() + needed - dependencies.ToInt64()) / 2);
            var chars = new char[remaining];
            Marshal.Copy(dependencies, chars, 0, chars.Length);
            try
            {
                int start = 0;
                for (int i = 0; i < chars.Length; i++)
                {
                    if (chars[i] != '\0') continue;
                    if (i == start) break;
                    string file = new(chars, start, i - start);
                    if (Path.IsPathFullyQualified(file) && (Path.GetFileName(file).Equals("KMFS85O5.DLL", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(file).Equals("KMFS879H.DLL", StringComparison.OrdinalIgnoreCase))) return file;
                    start = i + 1;
                }
            }
            finally { Array.Clear(chars); }
            throw new IOException("ExactDriverComponentNotFound");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static (uint Type, byte[]? Bytes) ReadValue(SafePrinter printer, string name)
    {
        uint result = GetPrinterData(printer, name, out uint type, null, 0, out uint needed);
        if (result is not (0 or 234) || needed is < 1 or > 16384) return (type, null);
        byte[] bytes = new byte[needed];
        result = GetPrinterData(printer, name, out type, bytes, needed, out uint actual);
        if (result != 0 || actual != needed) { CryptographicOperations.ZeroMemory(bytes); return (type, null); }
        return (type, bytes);
    }

    private static byte[]? Ansi(string name)
    {
        int count = WideCharToMultiByte(0, 0x400, name, name.Length, null, 0, IntPtr.Zero, out _);
        if (count < 1 || count > 1024) return null;
        byte[] bytes = new byte[count];
        if (WideCharToMultiByte(0, 0x400, name, name.Length, bytes, count, IntPtr.Zero, out bool substituted) != count || substituted)
        { CryptographicOperations.ZeroMemory(bytes); return null; }
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterDefaults { public IntPtr DataType; public IntPtr DevMode; public uint DesiredAccess; }
    private sealed class SafePrinter : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafePrinter() : base(true) { }
        protected override bool ReleaseHandle() => ClosePrinter(handle);
    }
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(string name, out SafePrinter printer, ref PrinterDefaults defaults);
    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr printer);
    [DllImport("winspool.drv", EntryPoint = "GetPrinterDriverW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPrinterDriver(SafePrinter printer, string? environment, uint level, IntPtr data, uint size, out uint needed);
    [DllImport("winspool.drv", EntryPoint = "GetPrinterDataW", CharSet = CharSet.Unicode)]
    private static extern uint GetPrinterData(SafePrinter printer, string name, out uint type, [Out] byte[]? data, uint size, out uint needed);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WideCharToMultiByte(uint page, uint flags, string source, int count, byte[]? output, int capacity, IntPtr defaultChar, [MarshalAs(UnmanagedType.Bool)] out bool substituted);
}

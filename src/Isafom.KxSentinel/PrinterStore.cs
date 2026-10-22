using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Isafom.KxResearch;

namespace Isafom.KxSentinel;

internal sealed record PrinterInfo(string Name, string Driver, string Port, string Comment, uint Attributes, uint Status, uint Jobs);

// All mutations are restricted to the fresh queue identified by a random owner
// marker. No SetPrinterData/SetPrinter call targets the reference queue.
internal static class PrinterStore
{
    internal static SafePrinter Open(string name, uint access = 8)
    {
        var defaults = new Defaults { Access = access };
        if (!OpenPrinter(name, out var result, ref defaults)) throw new IOException("PrinterOpenFailed");
        return result;
    }

    internal static PrinterInfo[] Inventory()
    {
        EnumPrinters(2, null, 2, IntPtr.Zero, 0, out uint needed, out _);
        if (needed == 0) return [];
        using var buffer = new Buffer(needed);
        if (!EnumPrinters(2, null, 2, buffer.Pointer, needed, out _, out uint count) || count > needed / Marshal.SizeOf<Info2>())
            throw new IOException("InventoryUnavailable");
        return Enumerable.Range(0, checked((int)count)).Select(i => Info(buffer, buffer.Pointer + i * Marshal.SizeOf<Info2>())).ToArray();
    }

    internal static PrinterInfo Info(SafePrinter printer)
    {
        using var buffer = Get(printer, 2);
        return Info(buffer, buffer.Pointer);
    }

    private static PrinterInfo Info(Buffer buffer, IntPtr pointer)
    {
        var info = Marshal.PtrToStructure<Info2>(pointer);
        return new(buffer.Text(info.Name), buffer.Text(info.Driver), buffer.Text(info.Port), buffer.Text(info.Comment), info.Attributes, info.Status, info.Jobs);
    }

    internal static void RequireOwned(SafePrinter printer, string token)
    {
        if (!TrialPolicy.Owned(Info(printer), token)) throw new IOException("OwnedPausedQueueRequired");
    }

    internal static bool ReferenceEndpoint(PrinterInfo printer)
    {
        if (!TrialPolicy.Model(printer.Driver) || printer.Port.IndexOfAny(['\\', '/']) >= 0) return false;
        using var port = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports\" + printer.Port, false);
        if (port is null) return false;
        bool host = new[] { port.GetValue("HostName"), port.GetValue("IPAddress") }.OfType<string>().Any(s => s.Equals("stampante.isafom.cnr.it", StringComparison.OrdinalIgnoreCase));
        return host && port.GetValue("PortNumber") is int number && number == 9100 && port.GetValue("Protocol") is int protocol && protocol == 1;
    }

    internal static FilePortReport CheckFilePort()
    {
        bool portsRead = false;
        try
        {
            EnumPorts(null, 2, IntPtr.Zero, 0, out uint needed, out _);
            if (needed == 0) return FilePortPolicy.Unavailable("PortEnumerationUnavailable", Marshal.GetLastWin32Error());
            var ports = new List<PortDescriptor>();
            using (var buffer = new Buffer(needed))
            {
                if (!EnumPorts(null, 2, buffer.Pointer, needed, out _, out uint count))
                    return FilePortPolicy.Unavailable("PortEnumerationUnavailable", Marshal.GetLastWin32Error());
                if (count > needed / Marshal.SizeOf<PortInfo2>()) throw new IOException();
                for (int i = 0; i < count; i++)
                {
                    var port = Marshal.PtrToStructure<PortInfo2>(buffer.Pointer + i * Marshal.SizeOf<PortInfo2>());
                    ports.Add(new(buffer.Text(port.Name), buffer.Text(port.Monitor), port.Type));
                }
            }
            portsRead = true;
            EnumMonitors(null, 2, IntPtr.Zero, 0, out needed, out _);
            if (needed == 0) return FilePortPolicy.Unavailable("MonitorEnumerationUnavailable", Marshal.GetLastWin32Error()) with { PortEnumerationSucceeded = true };
            var monitors = new List<MonitorDescriptor>();
            using (var buffer = new Buffer(needed))
            {
                if (!EnumMonitors(null, 2, buffer.Pointer, needed, out _, out uint count))
                    return FilePortPolicy.Unavailable("MonitorEnumerationUnavailable", Marshal.GetLastWin32Error()) with { PortEnumerationSucceeded = true };
                if (count > needed / Marshal.SizeOf<MonitorInfo2>()) throw new IOException();
                for (int i = 0; i < count; i++)
                {
                    var monitor = Marshal.PtrToStructure<MonitorInfo2>(buffer.Pointer + i * Marshal.SizeOf<MonitorInfo2>());
                    monitors.Add(new(buffer.Text(monitor.Name), buffer.Text(monitor.Dll)));
                }
            }
            return FilePortPolicy.Evaluate([.. ports], [.. monitors]);
        }
        catch { return FilePortPolicy.Unavailable("PortMetadataInvalid") with { PortEnumerationSucceeded = portsRead }; }
    }

    internal static string Component(SafePrinter printer)
    {
        GetPrinterDriver(printer, null, 3, IntPtr.Zero, 0, out uint needed);
        using var buffer = new Buffer(needed);
        if (!GetPrinterDriver(printer, null, 3, buffer.Pointer, needed, out _) || needed < 80) throw new IOException("DriverMetadataUnavailable");
        IntPtr dependencies = Marshal.ReadIntPtr(buffer.Pointer, 56);
        int offset = buffer.Offset(dependencies, 2);
        for (int i = offset; i < needed - 1;)
        {
            string file = buffer.Text(buffer.Pointer + i);
            if (file.Length == 0) break;
            if (Path.IsPathFullyQualified(file) && (Path.GetFileName(file).Equals("KMFS85O5.DLL", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(file).Equals("KMFS879H.DLL", StringComparison.OrdinalIgnoreCase)))
            {
                // The pure codec checks the complete original SHA-256, without
                // loading or running the DLL, before any queue can be created.
                using var proof = new Kx85PrivateCodeCodec(file);
                return file;
            }
            i = checked(i + (file.Length + 1) * 2);
        }
        throw new IOException("ExactDriverComponentNotFound");
    }

    internal static SafePrinter Create(string driver, string token)
    {
        var info = new Info2 { Name = Marshal.StringToHGlobalUni(TrialPolicy.QueueName(token)),
            Driver = Marshal.StringToHGlobalUni(driver), Port = Marshal.StringToHGlobalUni("FILE:"),
            Comment = Marshal.StringToHGlobalUni(TrialPolicy.MarkerPrefix + token),
            Processor = Marshal.StringToHGlobalUni("winprint"), DataType = Marshal.StringToHGlobalUni("RAW"),
            Attributes = 0x40, Priority = 1, DefaultPriority = 1 };
        try
        {
            var handle = AddPrinter(null, 2, ref info);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("TestQueueCreationFailed"); }
            // No job submission API exists in this executable. FILE: is also
            // unrelated to the physical printer before the pause takes effect.
            if (!SetPrinter(handle, 0, IntPtr.Zero, 1))
            {
                // This handle came directly from AddPrinter in this call. It
                // cannot denote a pre-existing queue chosen by name.
                if (Info(handle).Jobs == 0) DeletePrinter(handle);
                handle.Dispose(); throw new IOException("TestQueuePauseFailed");
            }
            return handle;
        }
        finally
        {
            foreach (IntPtr pointer in new[] { info.Name, info.Driver, info.Port, info.Comment, info.Processor, info.DataType }) Marshal.FreeHGlobal(pointer);
        }
    }

    internal static bool Remove(SafePrinter printer, string token)
    {
        RequireOwned(printer, token);
        return DeletePrinter(printer);
    }
    internal static bool RemoveAuthorizedTest(SafePrinter printer, NativeTarget target)
    {
        target.Require(printer);
        return DeletePrinter(printer);
    }

    internal static (bool Validated, bool Global, bool User) CommitNativeDevMode(SafePrinter printer, string token, IntPtr nativeMode, NativeTarget? target = null)
    {
        string queueName = target?.Name ?? TrialPolicy.QueueName(token);
        void Require() { if (target is null) RequireOwned(printer, token); else target.Require(printer); }
        Require();
        if (nativeMode == IntPtr.Zero) return (false, false, false);
        int length = unchecked((ushort)Marshal.ReadInt16(nativeMode, 68)) + unchecked((ushort)Marshal.ReadInt16(nativeMode, 70));
        int needed = DocumentProperties(IntPtr.Zero, printer, queueName, IntPtr.Zero, IntPtr.Zero, 0);
        if (length < 220 || length > 65536 || needed != length) return (false, false, false);
        IntPtr normalized = Marshal.AllocHGlobal(needed);
        IntPtr info = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            // Only documented public DEVMODE fields are edited. The driver
            // normalizes these together with its own complete private tail.
            Marshal.WriteInt32(nativeMode, 72, Marshal.ReadInt32(nativeMode, 72) | 0x800);
            Marshal.WriteInt16(nativeMode, 92, 1); // DMCOLOR_MONOCHROME
            // The complete DEVMODE comes from the original vendor setters.
            // No private-byte patch. Validate through the documented API with
            // DM_IN_BUFFER|DM_OUT_BUFFER; never DM_IN_PROMPT.
            if (DocumentProperties(IntPtr.Zero, printer, queueName, normalized, nativeMode, 10) != 1)
                return (false, false, false);
            if ((Marshal.ReadInt32(normalized, 72) & 0x800) == 0 || Marshal.ReadInt16(normalized, 92) != 1)
                return (false, false, false);
            Marshal.WriteIntPtr(info, normalized);
            Require();
            bool global = SetPrinter(printer, 8, info, 0);
            Require();
            bool user = global && SetPrinter(printer, 9, info, 0);
            return (true, global, user);
        }
        finally
        {
            for (int i = 0; i < needed; i++) Marshal.WriteByte(normalized, i, 0);
            Marshal.FreeHGlobal(normalized); Marshal.FreeHGlobal(info);
        }
    }

    internal static string DefaultName()
    {
        uint count = 0;
        GetDefaultPrinter(null, ref count);
        if (count == 0) return "";
        if (count > 2048) throw new IOException("DefaultPrinterUnavailable");
        var name = new char[count];
        if (!GetDefaultPrinter(name, ref count)) throw new IOException("DefaultPrinterUnavailable");
        return new string(name).TrimEnd('\0');
    }

    internal static bool CompletePending(SafePrinter printer, NativeTarget target, string pending, string completed, string ownerValueName, string displayComment)
    {
        target.Require(printer);
        string originalComment = Info(printer).Comment;
        if (originalComment == displayComment && StringValue(printer, ownerValueName) == completed) return true;
        bool wasPending = originalComment == pending;
        if (!wasPending && originalComment != completed) return false;
        // Persist identity before replacing the legacy comment. An interruption
        // leaves either the old marker or the new comment plus its owner record.
        // No names, accounting codes or DEVMODE bytes are stored in this value.
        byte[] owner = Encoding.Unicode.GetBytes(completed + '\0');
        target.Require(printer);
        if (SetPrinterData(printer, ownerValueName, 1, owner, checked((uint)owner.Length)) != 0 ||
            StringValue(printer, ownerValueName) != completed) return false;
        using var data = Get(printer, 2);
        var info = Marshal.PtrToStructure<Info2>(data.Pointer);
        IntPtr comment = Marshal.StringToHGlobalUni(displayComment);
        try
        {
            target.Require(printer);
            // A completed legacy queue is only relabelled: preserve its pause.
            if (wasPending && !SetPrinter(printer, 0, IntPtr.Zero, 2)) return false;
            info.Comment = comment;
            // GetPrinter(2) can return a full security descriptor. Updating a
            // comment must not attempt to reassign its owner or permissions.
            info.Security = IntPtr.Zero;
            Marshal.StructureToPtr(info, data.Pointer, false);
            if (!SetPrinter(printer, 2, data.Pointer, 0)) return false;
            target.Require(printer);
            return Info(printer).Comment == displayComment && StringValue(printer, ownerValueName) == completed;
        }
        finally { Marshal.FreeHGlobal(comment); }
    }

    internal static string? StringValue(SafePrinter printer, string name)
    {
        uint status = GetPrinterData(printer, name, out uint type, null, 0, out uint needed);
        if (status == 2) return null;
        if (status is not (0 or 234) || type != 1 || needed is < 2 or > 512 || needed % 2 != 0) return null;
        byte[] value = new byte[needed];
        if (GetPrinterData(printer, name, out type, value, needed, out uint actual) != 0 || type != 1 || actual != needed ||
            value[^1] != 0 || value[^2] != 0) return null;
        try { return new UnicodeEncoding(false, false, true).GetString(value, 0, value.Length - 2); }
        catch (DecoderFallbackException) { return null; }
    }

    internal static Buffer Get(SafePrinter printer, uint level)
    {
        GetPrinter(printer, level, IntPtr.Zero, 0, out uint needed);
        var buffer = new Buffer(needed);
        if (!GetPrinter(printer, level, buffer.Pointer, needed, out _)) { buffer.Dispose(); throw new IOException("PrinterReadUnavailable"); }
        return buffer;
    }

    internal static byte[]? Value(SafePrinter printer, string name)
    {
        uint status = GetPrinterData(printer, name, out _, null, 0, out uint needed);
        if (status == 2) return null;
        if (status is not (0 or 234) || needed > 16384) throw new IOException("SnapshotUnavailable");
        byte[] result = new byte[needed];
        if (GetPrinterData(printer, name, out _, result, needed, out uint actual) != 0 || actual != needed)
        { CryptographicOperations.ZeroMemory(result); throw new IOException("SnapshotUnavailable"); }
        return result;
    }

    internal sealed class Buffer : IDisposable
    {
        internal IntPtr Pointer { get; private set; }
        internal int Length { get; }
        internal Buffer(uint length)
        {
            if (length is < 8 or > 2 * 1024 * 1024) throw new IOException("BoundedBufferRequired");
            Length = (int)length; Pointer = Marshal.AllocHGlobal(Length);
        }
        internal int Offset(IntPtr pointer, int size)
        {
            long offset = pointer.ToInt64() - Pointer.ToInt64();
            if (offset < 0 || offset > Length - size) throw new IOException("InvalidBufferPointer");
            return (int)offset;
        }
        internal string Text(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return "";
            int offset = Offset(pointer, 2);
            for (int size = 0; offset + size + 1 < Length && size <= 4096; size += 2)
                if (Marshal.ReadInt16(pointer, size) == 0) return Marshal.PtrToStringUni(pointer, size / 2)!;
            throw new IOException("UnterminatedMetadata");
        }
        internal byte[] DevMode()
        {
            IntPtr mode = Marshal.ReadIntPtr(Pointer);
            if (mode == IntPtr.Zero) return [];
            Offset(mode, 72);
            int length = unchecked((ushort)Marshal.ReadInt16(mode, 68)) + unchecked((ushort)Marshal.ReadInt16(mode, 70));
            if (length < 220 || length > 65536) throw new IOException("InvalidDevMode");
            Offset(mode, length);
            byte[] result = new byte[length]; Marshal.Copy(mode, result, 0, length); return result;
        }
        public void Dispose()
        {
            if (Pointer == IntPtr.Zero) return;
            // Clear unmanaged snapshots too; none are written to disk.
            for (int i = 0; i < Length; i++) Marshal.WriteByte(Pointer, i, 0);
            Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero;
        }
    }

    internal sealed class SafePrinter : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafePrinter() : base(true) { }
        protected override bool ReleaseHandle() => ClosePrinter(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Defaults { public IntPtr DataType, DevMode; public uint Access; }
    [StructLayout(LayoutKind.Sequential)] private struct PortInfo2 { public IntPtr Name, Monitor, Description; public uint Type, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo2 { public IntPtr Name, Environment, Dll; }
    [StructLayout(LayoutKind.Sequential)] private struct Info2
    {
        public IntPtr Server, Name, Share, Port, Driver, Comment, Location, DevMode, Separator, Processor, DataType, Parameters, Security;
        public uint Attributes, Priority, DefaultPriority, Start, Until, Status, Jobs, Ppm;
    }
    [DllImport("winspool.drv", EntryPoint="OpenPrinterW", CharSet=CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenPrinter(string name, out SafePrinter printer, ref Defaults defaults);
    [DllImport("winspool.drv")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClosePrinter(IntPtr printer);
    [DllImport("winspool.drv", EntryPoint="GetPrinterW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPrinter(SafePrinter printer,uint level,IntPtr data,uint size,out uint needed);
    [DllImport("winspool.drv", EntryPoint="EnumPrintersW", CharSet=CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumPrinters(uint flags,string? name,uint level,IntPtr data,uint size,out uint needed,out uint count);
    [DllImport("winspool.drv", EntryPoint="EnumPortsW", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumPorts(string? server,uint level,IntPtr data,uint size,out uint needed,out uint count);
    [DllImport("winspool.drv", EntryPoint="EnumMonitorsW", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumMonitors(string? server,uint level,IntPtr data,uint size,out uint needed,out uint count);
    [DllImport("winspool.drv", EntryPoint="GetPrinterDriverW", CharSet=CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPrinterDriver(SafePrinter printer,string? environment,uint level,IntPtr data,uint size,out uint needed);
    [DllImport("winspool.drv", EntryPoint="AddPrinterW", CharSet=CharSet.Unicode)] private static extern SafePrinter AddPrinter(string? server,uint level,ref Info2 info);
    [DllImport("winspool.drv", EntryPoint="SetPrinterW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetPrinter(SafePrinter printer,uint level,IntPtr data,uint command);
    [DllImport("winspool.drv", EntryPoint="DocumentPropertiesW", CharSet=CharSet.Unicode)] private static extern int DocumentProperties(IntPtr window,SafePrinter printer,string device,IntPtr output,IntPtr input,uint mode);
    [DllImport("winspool.drv")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeletePrinter(SafePrinter printer);
    [DllImport("winspool.drv", EntryPoint="GetDefaultPrinterW", CharSet=CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDefaultPrinter([Out] char[]? name,ref uint length);
    [DllImport("winspool.drv", EntryPoint="GetPrinterDataW", CharSet=CharSet.Unicode)] private static extern uint GetPrinterData(SafePrinter printer,string name,out uint type,[Out] byte[]? data,uint size,out uint needed);
    [DllImport("winspool.drv", EntryPoint="SetPrinterDataW", CharSet=CharSet.Unicode)] private static extern uint SetPrinterData(SafePrinter printer,string name,uint type,byte[] data,uint size);
}

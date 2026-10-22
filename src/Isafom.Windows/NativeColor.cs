using System.Runtime.InteropServices;

namespace Isafom.Windows;

public static class NativeColor
{
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPrinter(IntPtr handle, uint level, IntPtr buffer, uint size, out uint needed);
    [DllImport("winspool.drv")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr handle);

    // Public DEVMODEW header only; driver-private data is never copied or exported.
    public static bool? Read(string queue, uint level)
    {
        if (level is not (8 or 9)) throw new ArgumentOutOfRangeException(nameof(level));
        if (!OpenPrinter(queue, out var printer, IntPtr.Zero)) return null;
        IntPtr buffer = IntPtr.Zero;
        uint size = 0;
        try
        {
            GetPrinter(printer, level, IntPtr.Zero, 0, out size);
            if (size < IntPtr.Size || size > 1024 * 1024) return null;
            buffer = Marshal.AllocHGlobal((int)size);
            if (!GetPrinter(printer, level, buffer, size, out _)) return null;
            var dm = Marshal.ReadIntPtr(buffer);
            var start = buffer.ToInt64();
            var position = dm.ToInt64();
            if (dm == IntPtr.Zero || position < start || position + 94 > start + size) return null;
            // dmSize:68, dmFields:72, dmColor:92 in the documented Unicode DEVMODEW.
            if ((ushort)Marshal.ReadInt16(dm, 68) < 94 || (Marshal.ReadInt32(dm, 72) & 0x800) == 0) return null;
            var color = Marshal.ReadInt16(dm, 92);
            return color is 1 ? true : color is 2 ? false : null;
        }
        finally
        {
            // Spooler may have returned a private tail; clear it in memory before freeing.
            if (buffer != IntPtr.Zero) { for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0); Marshal.FreeHGlobal(buffer); }
            ClosePrinter(printer);
        }
    }
}

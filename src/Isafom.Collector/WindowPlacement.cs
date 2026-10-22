using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Isafom.Collector;

internal readonly record struct PixelBounds(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

internal static class WindowGeometry
{
    // All placement arithmetic uses physical pixels. Do not mix rcWork with
    // WPF DIPs or the primary monitor's SystemParameters.WorkArea.
    public static PixelBounds Fit(PixelBounds work, double scale, PixelBounds? existing = null, double initialWidth = 1020, double initialHeight = 800, bool preserveInitialAspect = false)
    {
        int gap = Math.Min((int)Math.Ceiling(12 * scale), Math.Max(0, Math.Min(work.Width, work.Height) / 20));
        int width = Math.Max(1, Math.Min(existing?.Width ?? (int)Math.Round(initialWidth * scale), work.Width - 2 * gap));
        int height = Math.Max(1, Math.Min(existing?.Height ?? (int)Math.Round(initialHeight * scale), work.Height - 2 * gap));
        if (existing is null && preserveInitialAspect)
        {
            double fit = Math.Min(1, Math.Min((work.Width - 2 * gap) * .90 / (initialWidth * scale),
                (work.Height - 2 * gap) * .90 / (initialHeight * scale)));
            width = Math.Max(1, (int)Math.Floor(initialWidth * scale * fit));
            height = Math.Max(1, (int)Math.Floor(initialHeight * scale * fit));
        }
        int left = existing is null ? work.Left + (work.Width - width) / 2 : Math.Clamp(existing.Value.Left, work.Left + gap, work.Right - gap - width);
        int top = existing is null ? work.Top + (work.Height - height) / 2 : Math.Clamp(existing.Value.Top, work.Top + gap, work.Bottom - gap - height);
        return new(left, top, width, height);
    }
}

internal sealed class WindowPlacement(Window window, double initialWidth = 1020, double initialHeight = 800, double minimumHeight = 420, double minimumWidth = 640, bool preserveInitialAspect = false)
{
    private bool updating;
    private bool scheduled;
    private HwndSource? source;
    private IntPtr handle;

    public void Attach()
    {
        window.SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(window).Handle;
            source = HwndSource.FromHwnd(handle);
            source?.AddHook(WindowMessage);
            Place(initial: true);
        };
        window.ContentRendered += (_, _) => Place(initial: false);
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Normal)
                window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Place(initial: false));
        };
        window.Closed += (_, _) => { source?.RemoveHook(WindowMessage); source = null; handle = IntPtr.Zero; };
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Windows/WPF continue to handle native chrome and WM_DPICHANGED.
        // Correct bounds afterwards, including display/work-area changes (RDP).
        if (!updating && !scheduled && message is 0x007e or 0x02e0 or 0x001a)
        {
            scheduled = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { scheduled = false; Place(initial: false); });
        }
        return IntPtr.Zero;
    }

    private void Place(bool initial)
    {
        if (handle == IntPtr.Zero || updating || window.WindowState != WindowState.Normal) return;
        updating = true;
        try
        {
            IntPtr monitor = initial && GetCursorPos(out var point)
                ? MonitorFromPoint(point, 2) : MonitorFromWindow(handle, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(handle, out var current)) return;
            var work = new PixelBounds(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
            if (work.Width < 1 || work.Height < 1) return;
            // Move the HWND to the chosen monitor before reading its DPI.
            // This also permits WPF to receive the monitor's DPI transition.
            if (initial)
                SetWindowPos(handle, IntPtr.Zero, work.Left + work.Width / 4, work.Top + work.Height / 4, 0, 0, 0x0001 | 0x0004 | 0x0010);
            uint dpi = GetDpiForWindow(handle);
            double scale = dpi == 0 ? 1 : dpi / 96d;
            var existing = initial ? (PixelBounds?)null : new(current.Left, current.Top, current.Right - current.Left, current.Bottom - current.Top);
            var fit = WindowGeometry.Fit(work, scale, existing, initialWidth, initialHeight, preserveInitialAspect);
            window.MinWidth = Math.Min(minimumWidth, fit.Width / scale);
            window.MinHeight = Math.Min(minimumHeight, fit.Height / scale);
            SetWindowPos(handle, IntPtr.Zero, fit.Left, fit.Top, fit.Width, fit.Height, 0x0004 | 0x0010);
        }
        finally { updating = false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}

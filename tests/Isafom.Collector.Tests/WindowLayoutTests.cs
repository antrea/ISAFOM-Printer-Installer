using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Isafom.Collector;

internal static class WindowLayoutTests
{
    public static void Geometry(Action<bool, string> assert)
    {
        // Simulate physical monitor work areas and effective scaling without
        // changing display settings on the development PC.
        var displays = new[]
        {
            new PixelBounds(0, 0, 1366, 728), new PixelBounds(0, 40, 1920, 1040),
            new PixelBounds(-1920, -400, 1920, 1040), new PixelBounds(1920, 0, 2560, 1400),
            new PixelBounds(0, 0, 1024, 728), new PixelBounds(0, 0, 800, 560)
        };
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2d })
        foreach (var work in displays)
        {
            var fit = WindowGeometry.Fit(work, scale);
            assert(Contains(work, fit), $"Initial complete frame fits {work.Width}x{work.Height} at {scale * 100}% (origin {work.Left},{work.Top})");
            var recovered = WindowGeometry.Fit(work, scale, new(work.Left - 500, work.Top - 300, 3000, 2000));
            assert(Contains(work, recovered), $"Oversized/offscreen frame recovered at {work.Width}x{work.Height}, {scale * 100}%");
        }
    }
    private static bool Contains(PixelBounds work, PixelBounds frame) => frame.Width > 0 && frame.Height > 0 &&
        frame.Left >= work.Left && frame.Top >= work.Top && frame.Right <= work.Right && frame.Bottom <= work.Bottom;

    public static void NativeAndContent(CollectorWindow window, Action<bool, string> assert)
    {
        assert(window.WindowStyle == WindowStyle.SingleBorderWindow && window.ResizeMode == ResizeMode.CanResize && !window.AllowsTransparency,
            "Window preserves native title bar and resize/minimize/maximize controls");
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        long style = GetWindowLongPtr(handle, -16).ToInt64();
        assert((style & 0x00cf0000) == 0x00cf0000, "Actual HWND has caption, system menu, resize border, minimize and maximize styles");
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        assert(GetMonitorInfo(MonitorFromWindow(handle, 2), ref info) && GetWindowRect(handle, out _), "Native monitor/frame bounds available");
        GetWindowRect(handle, out var rect);
        assert(rect.Left >= info.Work.Left && rect.Top >= info.Work.Top && rect.Right <= info.Work.Right && rect.Bottom <= info.Work.Bottom,
            "Actual native frame including caption starts within current monitor work area");
        assert(double.IsPositiveInfinity(window.MaxWidth) && double.IsPositiveInfinity(window.MaxHeight), "No primary-monitor maximum prevents native maximize on another display");

        var content = (FrameworkElement)window.Content;
        var viewport = (ScrollViewer)window.FindName("LayoutViewport");
        foreach (var size in new[] { new Size(1000, 740), new Size(720, 480), new Size(600, 360) })
        {
            content.Measure(size); content.Arrange(new System.Windows.Rect(size)); content.UpdateLayout();
            assert(viewport.ViewportHeight > 0 && viewport.ViewportWidth > 0, $"Usable scroll viewport at {size.Width}x{size.Height} DIPs");
            assert(size.Height > 600 || viewport.ScrollableHeight > 0, $"Short content remains reachable by vertical scrolling at {size.Width}x{size.Height} DIPs");
            var button = (Button)window.FindName("CaptureButton");
            var buttonRect = button.TransformToAncestor(content).TransformBounds(new System.Windows.Rect(button.RenderSize));
            assert(buttonRect.Left >= 0 && buttonRect.Right <= size.Width + 1, $"Main collection action fits available width at {size.Width} DIPs");
            viewport.ScrollToBottom(); content.UpdateLayout();
            assert(viewport.VerticalOffset > 0 || viewport.ScrollableHeight == 0, $"Bottom of layout can be reached at {size.Height} DIPs");
            viewport.ScrollToTop(); content.UpdateLayout();
        }

        // Real caption check on this application's own HWND. Keep the test
        // completely transparent, off the taskbar, and never take focus.
        window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        AssertCaption("Normal");
        window.WindowState = WindowState.Maximized;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        assert(IsZoomed(handle), "Native maximize works without a primary-monitor size cap");
        AssertCaption("Maximized");
        window.WindowState = WindowState.Minimized;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        assert(IsIconic(handle), "Native minimize works");
        window.WindowState = WindowState.Normal;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        AssertCaption("Restored");
        window.Hide();

        void AssertCaption(string state)
        {
            var title = new TitleBarInfo { Size = Marshal.SizeOf<TitleBarInfo>(), States = new uint[6], Rects = new Rect[6] };
            SendMessage(handle, 0x033f, IntPtr.Zero, ref title);
            foreach (int index in new[] { 2, 3, 5 })
            {
                var button = title.Rects[index];
                assert((title.States[index] & 0x18001) == 0 && button.Right > button.Left && button.Bottom > button.Top &&
                    button.Left >= info.Work.Left && button.Top >= info.Work.Top && button.Right <= info.Work.Right && button.Bottom <= info.Work.Bottom,
                    $"{state}: native caption button {index} available and within work area");
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct TitleBarInfo
    {
        public int Size; public Rect TitleBar;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public uint[] States;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public Rect[] Rects;
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, ref TitleBarInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
}

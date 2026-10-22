using System.Windows;

namespace Isafom.Windows;

internal readonly record struct AdaptiveSize(double Scale, bool RequiresScrolling);

internal static class AdaptiveLayout
{
    public const double Width = 900;
    public const double Height = 640;

    public static AdaptiveSize Fit(Size viewport, double dpiScale)
    {
        if (!double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
            viewport.Width <= 0 || viewport.Height <= 0) return new(1, false);
        if (!double.IsFinite(dpiScale) || dpiScale < 1) dpiScale = 1;
        // Keep close to the normal 100% physical text size. On an even smaller
        // viewport, scroll the complete surface instead of clipping controls or
        // reducing text indefinitely. Never change Windows' display settings.
        double minimum = .92 / dpiScale;
        double fit = Math.Min((viewport.Width - 2) / Width, (viewport.Height - 2) / Height);
        double scale = Math.Clamp(fit, minimum, 1);
        return new(scale, Width * scale > viewport.Width || Height * scale > viewport.Height);
    }
}

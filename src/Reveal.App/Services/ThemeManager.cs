using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Reveal.Platform.Native;

namespace Reveal.App.Services;

/// <summary>
/// Dark / light appearance. Every themed brush is referenced as a DynamicResource, so replacing
/// the brushes in the application resources restyles all open windows at once.
/// </summary>
public static class ThemeManager
{
    public const string Dark = "dark";
    public const string Light = "light";

    private static readonly Dictionary<string, (string Dark, string Light)> Colors = new()
    {
        ["Bg0"] = ("#0E0F12", "#F3F4F7"),
        ["Bg1"] = ("#15171B", "#FFFFFF"),
        ["Bg2"] = ("#1D2026", "#EEF0F4"),
        ["Bg3"] = ("#262A32", "#E2E5EB"),
        ["Border"] = ("#2A2E36", "#DCDFE6"),
        ["Text1"] = ("#EDEEF1", "#16181D"),
        ["Text2"] = ("#A3A7B0", "#4A505C"),
        ["Text3"] = ("#6C717B", "#868B97"),
        ["Accent"] = ("#7CA0FF", "#3E63DD"),
        ["AccentHover"] = ("#A0BAFF", "#2F51C4"),
        ["AccentDim"] = ("#2A3560", "#D9E2FB"),
        ["Success"] = ("#5FD39A", "#1E9E64"),
        ["Warning"] = ("#F0B35A", "#B46D12"),
        ["Danger"] = ("#F0707A", "#D6404E"),
        ["OnAccent"] = ("#0B0D14", "#FFFFFF"),
        ["OverlayBg"] = ("#FC111317", "#FCFFFFFF"),
    };

    public static string Current { get; private set; } = Dark;

    public static bool IsDark => Current == Dark;

    public static void Apply(string? theme)
    {
        Current = theme == Light ? Light : Dark;
        var resources = Application.Current.Resources;
        foreach (var (key, pair) in Colors)
        {
            resources[key] = Frozen(new SolidColorBrush(Parse(IsDark ? pair.Dark : pair.Light)));
        }

        resources["AccentGradient"] = Frozen(Gradient(new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) },
            IsDark ? new[] { "#A9C0FF", "#7CA0FF", "#6A7DF0" } : new[] { "#7E9BF2", "#4A6DE3", "#3550C8" }, 0, 0.55, 1));
        resources["OverlayEdge"] = Frozen(Gradient(new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) },
            IsDark ? new[] { "#4D9DB6FF", "#1FFFFFFF", "#14FFFFFF" } : new[] { "#663E63DD", "#1F000000", "#14000000" }, 0, 0.35, 1));
        resources["HeroBg"] = Frozen(Gradient(new RadialGradientBrush
        {
            Center = new Point(0.5, 0.32), GradientOrigin = new Point(0.5, 0.32), RadiusX = 0.9, RadiusY = 0.65,
        }, IsDark ? new[] { "#1B2030", "#15171B" } : new[] { "#E9EFFF", "#FFFFFF" }, 0, 1));

        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
    }

    /// <summary>Title bar that matches the theme (Windows 10 20H1+ dark mode; Windows 11 caption colours).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || window.WindowStyle == WindowStyle.None) return;
        var dark = IsDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var caption = ToColorRef(Colors["Bg0"]);
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        var text = ToColorRef(Colors["Text1"]);
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_TEXT_COLOR, ref text, sizeof(int));
        var border = ToColorRef(Colors["Border"]);
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        var round = 2; // DWMWCP_ROUND
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    private static int ToColorRef((string Dark, string Light) pair)
    {
        var c = Parse(IsDark ? pair.Dark : pair.Light);
        return c.R | (c.G << 8) | (c.B << 16); // COLORREF 0x00BBGGRR
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static T Gradient<T>(T brush, string[] colors, params double[] offsets) where T : GradientBrush
    {
        for (var i = 0; i < colors.Length; i++) brush.GradientStops.Add(new GradientStop(Parse(colors[i]), offsets[i]));
        return brush;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

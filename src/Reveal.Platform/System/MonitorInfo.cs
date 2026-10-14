using System.Runtime.InteropServices;
using System.Windows;
using Reveal.Platform.Native;

namespace Reveal.Platform.SystemIntegration;

/// <summary>Geometry of the monitor that contains a screen point, in physical pixels.</summary>
public sealed record MonitorGeometry(Rect Bounds, Rect WorkArea, double ScaleX, double ScaleY)
{
    public static MonitorGeometry FromPoint(int x, int y)
    {
        var handle = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return FromHandle(handle);
    }

    public static MonitorGeometry FromRect(Rect rect)
    {
        var r = new NativeMethods.RECT
        {
            Left = (int)rect.Left, Top = (int)rect.Top, Right = (int)rect.Right, Bottom = (int)rect.Bottom,
        };
        return FromHandle(NativeMethods.MonitorFromRect(ref r, NativeMethods.MONITOR_DEFAULTTONEAREST));
    }

    private static MonitorGeometry FromHandle(IntPtr handle)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(handle, ref info);

        uint dpiX = 96, dpiY = 96;
        try { NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out dpiX, out dpiY); }
        catch { /* very old Windows: 100% */ }

        return new MonitorGeometry(
            new Rect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height),
            new Rect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Width, info.rcWork.Height),
            dpiX / 96.0,
            dpiY / 96.0);
    }
}

public enum OverlaySide
{
    Below,
    Above,
}

/// <summary>Pure placement maths, kept separate so it can be unit tested for any monitor layout / scaling.</summary>
public static class OverlayPlacement
{
    /// <summary>
    /// Computes the top-left corner (physical pixels) for a window of <paramref name="width"/> x <paramref name="height"/>
    /// pixels placed next to <paramref name="anchor"/>: below it if there is room, else above, always inside the work area.
    /// <paramref name="reserve"/> is the height the card may grow to (translations arrive later): the side is chosen so it
    /// won't have to jump to the other side afterwards. <paramref name="forced"/> keeps the side already in use.
    /// </summary>
    public static Point Place(Rect anchor, double width, double height, Rect workArea, double gap,
        out OverlaySide side, OverlaySide? forced = null, double reserve = 0)
    {
        var need = Math.Max(height, reserve);
        var roomBelow = workArea.Bottom - (anchor.Bottom + gap);
        var roomAbove = anchor.Top - gap - workArea.Top;

        side = forced ?? (roomBelow >= need ? OverlaySide.Below
            : roomAbove >= need ? OverlaySide.Above
            : roomBelow >= roomAbove ? OverlaySide.Below : OverlaySide.Above);

        var x = anchor.Left;
        var y = side == OverlaySide.Below ? anchor.Bottom + gap : anchor.Top - gap - height;

        // If it still doesn't fit, slide it along the edge instead of leaving the screen.
        if (y + height > workArea.Bottom) y = workArea.Bottom - height;
        if (y < workArea.Top) y = workArea.Top;
        if (x + width > workArea.Right) x = workArea.Right - width;
        if (x < workArea.Left) x = workArea.Left;

        return new Point(Math.Round(x), Math.Round(y));
    }

    public static Point Place(Rect anchor, double width, double height, Rect workArea, double gap) =>
        Place(anchor, width, height, workArea, gap, out _);
}

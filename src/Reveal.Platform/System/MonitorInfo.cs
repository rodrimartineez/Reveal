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

/// <summary>Pure placement maths, kept separate so it can be unit tested for any monitor layout / scaling.</summary>
public static class OverlayPlacement
{
    /// <summary>
    /// Computes the top-left corner (physical pixels) for a window of <paramref name="width"/> x <paramref name="height"/>
    /// pixels placed next to <paramref name="anchor"/>: below it if there is room, else above, always inside the work area.
    /// </summary>
    public static Point Place(Rect anchor, double width, double height, Rect workArea, double gap)
    {
        var x = anchor.Left;
        double y;

        var below = anchor.Bottom + gap;
        var above = anchor.Top - gap - height;
        if (below + height <= workArea.Bottom) y = below;
        else if (above >= workArea.Top) y = above;
        else y = Math.Max(workArea.Top, workArea.Bottom - height); // neither fits: pin to the bottom edge

        if (x + width > workArea.Right) x = workArea.Right - width;
        if (x < workArea.Left) x = workArea.Left;
        if (y < workArea.Top) y = workArea.Top;

        return new Point(Math.Round(x), Math.Round(y));
    }
}

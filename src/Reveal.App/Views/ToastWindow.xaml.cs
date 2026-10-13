using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Reveal.Platform.Native;
using Reveal.Platform.SystemIntegration;

namespace Reveal.App.Views;

/// <summary>Tiny non-activating message ("Capture on", "Couldn't read the selection") near the bottom of the active monitor.</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer = new();
    private IntPtr _hwnd;

    public ToastWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var style = (long)NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
            style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | 0x20 /* WS_EX_TRANSPARENT: clicks pass through */;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
        };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => Hide();
            BeginAnimation(OpacityProperty, fade);
        };
    }

    public void ShowToast(string title, string? subtitle, Brush? dot, int milliseconds)
    {
        TitleText.Text = title;
        SubtitleText.Text = subtitle ?? "";
        SubtitleText.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        DotMark.Fill = dot ?? (Brush)FindResource("Success");

        NativeMethods.GetCursorPos(out var cursor);
        var monitor = MonitorGeometry.FromPoint(cursor.X, cursor.Y);

        _timer.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        if (!IsVisible)
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, (int)monitor.WorkArea.Left + 20, (int)monitor.WorkArea.Top + 20, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            Show();
        }
        else
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, (int)monitor.WorkArea.Left + 20, (int)monitor.WorkArea.Top + 20, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        UpdateLayout();

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var w = (int)Math.Round(ActualWidth * scale);
        var h = (int)Math.Round(ActualHeight * scale);
        var x = (int)(monitor.WorkArea.Left + (monitor.WorkArea.Width - w) / 2);
        var y = (int)(monitor.WorkArea.Bottom - h - 36 * scale);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        _timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _timer.Start();
    }
}

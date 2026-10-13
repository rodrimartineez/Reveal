using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Reveal.Core.Models;
using Reveal.Platform.Native;
using Reveal.Platform.Speech;
using Reveal.Platform.SystemIntegration;

namespace Reveal.App.Views;

public enum SaveUiState
{
    Idle,
    Saving,
    Saved,
    AlreadySaved,
    NeedsDocument,
    Error,
}

/// <summary>
/// The small floating card. It never takes focus from the application the text was selected in
/// (WS_EX_NOACTIVATE) and positions itself in physical pixels so it is right on any monitor / scaling.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double CardMargin = 32;
    private const double Gap = 10;

    private IntPtr _hwnd;
    private Rect _anchor;
    private bool _placedOnce;

    public event Action<EnglishVariant>? SpeakRequested;

    public event Action? SaveRequested;

    public event Action? CloseRequested;

    public event Action? SwapRequested;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        AmericanButton.Click += (_, _) => SpeakRequested?.Invoke(EnglishVariant.American);
        BritishButton.Click += (_, _) => SpeakRequested?.Invoke(EnglishVariant.British);
        SaveButton.Click += (_, _) => SaveRequested?.Invoke();
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
        SwapButton.Click += (_, _) => SwapRequested?.Invoke();
        Card.SizeChanged += (_, _) =>
        {
            if (IsVisible && _placedOnce) Reposition(animate: false);
        };
    }

    public IntPtr Handle => _hwnd;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = (long)NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));

        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int MA_NOACTIVATE = 3;
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }

            return IntPtr.Zero;
        });
    }

    public void EnsureHandle() => _hwnd = new WindowInteropHelper(this).EnsureHandle();

    // ------------------------------------------------------------------ content

    public void ApplyResult(LookupResult r, bool americanVoice, bool britishVoice, bool canSwap, string? swapLabel)
    {
        var failed = r.Status == LookupStatus.Failed;
        var pendingHead = r.Headword.Length == 0;

        // headword
        var head = pendingHead ? r.SourceText : r.Headword;
        var useCaps = !r.IsSentence && TextWords(head) <= 4;
        HeadwordText.Text = useCaps ? head.ToUpperInvariant() : head;
        HeadwordText.Foreground = pendingHead || failed ? (Brush)FindResource("Text2") : (Brush)FindResource("Text1");
        HeadwordText.FontSize = failed ? 15 : head.Length switch
        {
            <= 14 => 23,
            <= 24 => 19,
            <= 60 => 16,
            _ => 14.5,
        };
        HeadwordText.FontWeight = r.IsSentence || failed ? FontWeights.Normal : FontWeights.SemiBold;

        // phonetics
        var phon = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Ipa)) phon.Add(r.Ipa!);
        if (!string.IsNullOrWhiteSpace(r.PartOfSpeech)) phon.Add(r.PartOfSpeech!);
        PhoneticsText.Text = string.Join("  ·  ", phon);
        PhoneticsText.Visibility = phon.Count > 0 && !failed ? Visibility.Visible : Visibility.Collapsed;

        // translation
        var hasTranslation = r.Translations.Count > 0;
        TranslationText.Text = string.Join(" / ", r.Translations);
        TranslationText.Visibility = hasTranslation ? Visibility.Visible : Visibility.Collapsed;
        PendingText.Visibility = r.IsTranslationPending && !hasTranslation ? Visibility.Visible : Visibility.Collapsed;
        if (r.IsSentence && hasTranslation) TranslationText.FontSize = 14.5;
        else TranslationText.FontSize = 16;

        // pronunciation
        VoicePanel.Visibility = failed || pendingHead ? Visibility.Collapsed : Visibility.Visible;
        AmericanButton.Tag = americanVoice ? null : "missing";
        BritishButton.Tag = britishVoice ? null : "missing";
        AmericanButton.ToolTip = americanVoice ? "Escuchar con voz americana de Windows" : "No hay ninguna voz en-US instalada en Windows";
        BritishButton.ToolTip = britishVoice ? "Escuchar con voz británica de Windows" : "No hay ninguna voz en-GB instalada. Haz clic para ver cómo instalarla.";

        // synonyms / example
        SynonymsPanel.Visibility = r.Synonyms.Count > 0 && !failed ? Visibility.Visible : Visibility.Collapsed;
        SynonymsText.Text = string.Join("  ·  ", r.Synonyms);
        ExamplePanel.Visibility = !string.IsNullOrWhiteSpace(r.Example) && !failed ? Visibility.Visible : Visibility.Collapsed;
        ExampleText.Text = r.Example ?? "";

        // footer
        SaveButton.Visibility = failed || pendingHead ? Visibility.Collapsed : Visibility.Visible;
        SwapButton.Visibility = canSwap ? Visibility.Visible : Visibility.Collapsed;
        SwapText.Text = swapLabel ?? "";
        if (r.Message is { Length: > 0 }) SetStatus(r.Message, failed ? StatusKind.Danger : StatusKind.Warning);
        else SetStatus(null, StatusKind.None);

        if (!pendingHead) SetSaveState(SaveUiState.Idle, null);
    }

    private static int TextWords(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    public enum StatusKind
    {
        None,
        Info,
        Success,
        Warning,
        Danger,
    }

    public void SetStatus(string? text, StatusKind kind)
    {
        StatusText.Text = text ?? "";
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

        if (string.IsNullOrEmpty(text) || kind is StatusKind.None or StatusKind.Info)
        {
            StatusIcon.Visibility = Visibility.Collapsed;
            StatusText.Foreground = (Brush)FindResource("Text3");
            return;
        }

        var (brush, geometry) = kind switch
        {
            StatusKind.Success => ((Brush)FindResource("Success"), (Geometry)FindResource("IconCheck")),
            StatusKind.Danger => ((Brush)FindResource("Danger"), (Geometry)FindResource("IconWarning")),
            _ => ((Brush)FindResource("Warning"), (Geometry)FindResource("IconWarning")),
        };
        StatusIcon.Data = geometry;
        StatusIcon.Stroke = brush;
        StatusIcon.Visibility = Visibility.Visible;
        StatusText.Foreground = brush;
    }

    public void SetSaveState(SaveUiState state, string? message)
    {
        switch (state)
        {
            case SaveUiState.Idle:
                SaveButton.IsEnabled = true;
                SaveLabel.Text = "Guardar";
                SaveIcon.Data = (Geometry)FindResource("IconBookmark");
                break;
            case SaveUiState.Saving:
                SaveButton.IsEnabled = false;
                SaveLabel.Text = "Guardando…";
                break;
            case SaveUiState.Saved:
                SaveButton.IsEnabled = false;
                SaveLabel.Text = "Guardada";
                SaveIcon.Data = (Geometry)FindResource("IconCheck");
                SetStatus(message, StatusKind.Success);
                break;
            case SaveUiState.AlreadySaved:
                SaveButton.IsEnabled = false;
                SaveLabel.Text = "En tu lista";
                SaveIcon.Data = (Geometry)FindResource("IconCheck");
                SetStatus(message, StatusKind.Info);
                break;
            case SaveUiState.NeedsDocument:
                SaveButton.IsEnabled = true;
                SaveLabel.Text = "Elegir documento";
                SaveIcon.Data = (Geometry)FindResource("IconFolder");
                SetStatus(message, StatusKind.Warning);
                break;
            case SaveUiState.Error:
                SaveButton.IsEnabled = true;
                SaveLabel.Text = "Reintentar";
                SaveIcon.Data = (Geometry)FindResource("IconBookmark");
                SetStatus(message, StatusKind.Danger);
                break;
        }
    }

    public void SetPlaying(EnglishVariant? variant)
    {
        AmericanButton.Tag = variant == EnglishVariant.American ? "playing" : (AmericanButton.Tag as string == "missing" ? "missing" : null);
        BritishButton.Tag = variant == EnglishVariant.British ? "playing" : (BritishButton.Tag as string == "missing" ? "missing" : null);
    }

    // ------------------------------------------------------------------ placement

    /// <summary>Shows the card next to <paramref name="anchorPx"/> (screen pixels).</summary>
    public void Present(Rect anchorPx)
    {
        _anchor = anchorPx;
        var firstShow = !IsVisible;
        if (firstShow)
        {
            Opacity = 0;
            EnsureHandle();
            MoveToMonitor(anchorPx);
            Show();
            // Let WPF process the DPI change of the target monitor before measuring.
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        }
        else
        {
            MoveToMonitor(anchorPx);
        }

        Reposition(animate: firstShow);
        _placedOnce = true;
    }

    /// <summary>Ensures the window lives on the anchor's monitor so WPF uses that monitor's DPI for layout.</summary>
    private void MoveToMonitor(Rect anchorPx)
    {
        var monitor = MonitorGeometry.FromRect(anchorPx);
        var dpi = VisualTreeHelper.GetDpi(this);
        if (Math.Abs(dpi.DpiScaleX - monitor.ScaleX) > 0.01 || !IsOnMonitor(monitor))
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
                (int)monitor.WorkArea.Left + 24, (int)monitor.WorkArea.Top + 24, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private bool IsOnMonitor(MonitorGeometry monitor)
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var r)) return false;
        return r.Left >= monitor.Bounds.Left - 1 && r.Left < monitor.Bounds.Right
               && r.Top >= monitor.Bounds.Top - 1 && r.Top < monitor.Bounds.Bottom;
    }

    private void Reposition(bool animate)
    {
        var monitor = MonitorGeometry.FromRect(_anchor);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (Math.Abs(scale - monitor.ScaleX) > 0.01) scale = monitor.ScaleX;

        // Measure the card at the final width to know its height.
        var cardWidth = Width - 2 * CardMargin;
        Card.Measure(new Size(cardWidth, double.PositiveInfinity));
        var cardHeight = Math.Ceiling(Card.DesiredSize.Height);
        var windowHeightDip = cardHeight + 2 * CardMargin;
        if (Math.Abs(Height - windowHeightDip) > 0.5) Height = windowHeightDip;

        var cardPx = new Size(Math.Round(cardWidth * scale), Math.Round(cardHeight * scale));
        var topLeft = OverlayPlacement.Place(_anchor, cardPx.Width, cardPx.Height, monitor.WorkArea, Gap * scale);
        var margin = CardMargin * scale;

        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOPMOST,
            (int)Math.Round(topLeft.X - margin), (int)Math.Round(topLeft.Y - margin),
            (int)Math.Round(Width * scale), (int)Math.Round(windowHeightDip * scale),
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        _hit = new HitBox((int)topLeft.X, (int)topLeft.Y, (int)(topLeft.X + cardPx.Width), (int)(topLeft.Y + cardPx.Height));

        if (animate)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Card.RenderTransform = new TranslateTransform(0, 6);
            ((TranslateTransform)Card.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else if (Opacity < 1)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }
    }

    public void HideOverlay()
    {
        _placedOnce = false;
        _hit = null;
        if (!IsVisible) return;
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    private sealed record HitBox(int Left, int Top, int Right, int Bottom);

    private volatile HitBox? _hit;

    /// <summary>
    /// True when a screen pixel is on the card itself (not on the transparent shadow margin).
    /// Safe to call from any thread (used by the input hooks).
    /// </summary>
    public bool ContainsScreenPoint(int x, int y)
    {
        var hit = _hit;
        return hit is not null && x >= hit.Left && x < hit.Right && y >= hit.Top && y < hit.Bottom;
    }

    public bool IsShown => _hit is not null;
}

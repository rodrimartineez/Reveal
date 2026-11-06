using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Reveal.Core.Settings;
using Reveal.Core.Translation;
using Reveal.App.Services;

namespace Reveal.App.Views;

public partial class SettingsWindow : Window
{
    private sealed record AutoCloseChoice(int Seconds, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ThemeChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly AppHost _host;
    private bool _loading;
    private bool _downloading;
    private CancellationTokenSource? _cts;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        ThemeCombo.Items.Add(new ThemeChoice(ThemeManager.Dark, "Oscuro"));
        ThemeCombo.Items.Add(new ThemeChoice(ThemeManager.Light, "Claro"));
        AutoCloseCombo.Items.Add(new AutoCloseChoice(0, "Nunca (se cierra al hacer clic fuera o con Esc)"));
        foreach (var s in new[] { 8, 15, 30, 60 }) AutoCloseCombo.Items.Add(new AutoCloseChoice(s, $"A los {s} segundos"));

        Load();

        StartWithWindows.Click += (_, _) => Save(s => s.StartWithWindows = StartWithWindows.IsChecked == true);
        StartCapture.Click += (_, _) => Save(s => s.StartCaptureOnLaunch = StartCapture.IsChecked == true);
        CloseToTray.Click += (_, _) => Save(s => s.CloseToTray = CloseToTray.IsChecked == true);
        KeyboardSelection.Click += (_, _) => SaveAndRestartCapture(s => s.CaptureKeyboardSelection = KeyboardSelection.IsChecked == true);
        ClipboardFallback.Click += (_, _) => SaveAndRestartCapture(s => s.UseClipboardFallback = ClipboardFallback.IsChecked == true);
        OnlineFallback.Click += (_, _) => Save(s => s.AllowOnlineFallback = OnlineFallback.IsChecked == true);
        ThemeCombo.SelectionChanged += (_, _) =>
        {
            if (_loading || ThemeCombo.SelectedItem is not ThemeChoice t) return;
            Save(s => s.Theme = t.Id);
            ThemeManager.Apply(t.Id);
        };
        AutoCloseCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && AutoCloseCombo.SelectedItem is AutoCloseChoice c) Save(s => s.OverlayAutoCloseSeconds = c.Seconds);
        };
        RateSlider.ValueChanged += (_, e) =>
        {
            RateText.Text = $"{e.NewValue:0.0}×";
            if (!_loading) Save(s => s.SpeechRate = Math.Round(e.NewValue, 1));
        };

        PackDownload.Click += (_, _) => _ = DownloadAsync();
        PackDelete.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "¿Eliminar los modelos de traducción offline? Podrás volver a descargarlos cuando quieras.", "Reveal",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _host.Marian.Unload();
            _host.Models.DeleteAll();
            RefreshPack();
        };
        OpenLogs.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            Process.Start(new ProcessStartInfo(AppPaths.LogsDirectory) { UseShellExecute = true });
        };

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        AboutText.Text = $"Reveal {version?.ToString(3)}\nDatos léxicos: Wiktionary (CC BY-SA), Open English WordNet (CC BY), Tatoeba (CC BY), FrequencyWords (CC BY-SA). " +
                         "Traducción: Opus-MT (Helsinki-NLP, CC BY 4.0) en formato ONNX de Xenova. Voces: Windows.";
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var s = _host.Settings.Current;
            StartWithWindows.IsChecked = s.StartWithWindows;
            StartCapture.IsChecked = s.StartCaptureOnLaunch;
            CloseToTray.IsChecked = s.CloseToTray;
            KeyboardSelection.IsChecked = s.CaptureKeyboardSelection;
            ClipboardFallback.IsChecked = s.UseClipboardFallback;
            OnlineFallback.IsChecked = s.AllowOnlineFallback;
            AutoCloseCombo.SelectedItem = AutoCloseCombo.Items.OfType<AutoCloseChoice>().FirstOrDefault(c => c.Seconds == s.OverlayAutoCloseSeconds)
                                          ?? AutoCloseCombo.Items[0];
            ThemeCombo.SelectedItem = ThemeCombo.Items.OfType<ThemeChoice>().FirstOrDefault(c => c.Id == s.Theme) ?? ThemeCombo.Items[0];
            RateSlider.Value = s.SpeechRate;
            RateText.Text = $"{s.SpeechRate:0.0}×";
            RefreshPack();
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save(Action<AppSettings> mutate)
    {
        if (_loading) return;
        _host.Settings.Update(mutate);
    }

    /// <summary>Capture options are read when capture starts, so restart it if it is running.</summary>
    private void SaveAndRestartCapture(Action<AppSettings> mutate)
    {
        Save(mutate);
        if (!_host.CaptureActive) return;
        _host.StopCapture(silent: true);
        _host.StartCapture(silent: true);
    }

    private void RefreshPack()
    {
        var installed = _host.Models.AllInstalled;
        if (!_downloading)
        {
            PackStatus.Text = installed
                ? "Modelos Opus-MT instalados: la traducción inglés ↔ español es 100 % local."
                : "Los modelos de traducción no están instalados (≈ 230 MB, descarga única desde Hugging Face).";
            PackDownload.IsEnabled = !installed;
            PackDownload.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            PackDelete.IsEnabled = installed;
        }
    }

    private async Task DownloadAsync()
    {
        _downloading = true;
        _cts = new CancellationTokenSource();
        PackDownload.IsEnabled = false;
        PackProgress.Visibility = Visibility.Visible;
        PackProgress.Value = 0;
        try
        {
            var packs = ModelPack.All.ToList();
            var progress = new Progress<DownloadProgress>(p =>
            {
                var index = packs.FindIndex(x => x.Id == p.Pack);
                var fraction = p.BytesTotal > 0 && p.File.EndsWith(".onnx") ? (double)p.BytesDone / p.BytesTotal : 0;
                PackProgress.Value = Math.Min(1, (index + fraction * 0.95) / packs.Count);
                PackStatus.Text = $"Descargando {p.File} ({p.BytesDone / 1_048_576} MB)…";
            });
            await _host.Models.DownloadAllAsync(progress, _cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error("Model download failed", ex);
            PackStatus.Text = "No se pudo descargar: " + ex.Message;
        }
        finally
        {
            _downloading = false;
            PackProgress.Visibility = Visibility.Collapsed;
            RefreshPack();
        }
    }
}

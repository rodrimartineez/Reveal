using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Reveal.App.Services;
using Reveal.Core.Models;
using Reveal.Core.Translation;
using Reveal.Core.Vocabulary;
using Reveal.Platform.Hotkeys;
using Reveal.Platform.Speech;

namespace Reveal.App.Views;

public partial class MainWindow : Window
{
    private sealed record VoiceChoice(string? Id, string Display)
    {
        public override string ToString() => Display;
    }

    private readonly AppHost _host;
    private bool _refreshing;
    private string? _capturing; // "start" | "stop" | null
    private CancellationTokenSource? _downloadCts;
    private bool _downloading;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        CaptureToggle.Click += (_, _) =>
        {
            if (_refreshing) return;
            if (CaptureToggle.IsChecked == true) _host.StartCapture(silent: true);
            else _host.StopCapture(silent: true);
        };

        ChooseDocButton.Click += (_, _) => PromptForDocument();
        NewDocButton.Click += (_, _) => CreateDocument();
        OpenDocButton.Click += (_, _) => OpenDocument();
        SettingsButton.Click += (_, _) => _host.ShowSettings();
        QuitButton.Click += (_, _) => _host.Exit();

        AmericanCombo.SelectionChanged += (_, _) => OnVoiceChanged(EnglishVariant.American, AmericanCombo);
        BritishCombo.SelectionChanged += (_, _) => OnVoiceChanged(EnglishVariant.British, BritishCombo);
        AmericanTest.Click += (_, _) => _ = TestVoiceAsync(EnglishVariant.American);
        BritishTest.Click += (_, _) => _ = TestVoiceAsync(EnglishVariant.British);
        OpenSpeechSettings.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true });

        StartKeyButton.Click += (_, _) => BeginHotkeyCapture("start");
        StopKeyButton.Click += (_, _) => BeginHotkeyCapture("stop");
        PreviewKeyDown += OnPreviewKeyDown;

        PackButton.Click += (_, _) => _ = DownloadPackAsync();

        HistoryClear.Click += (_, _) => _host.History.Clear();
        _host.History.Changed += RefreshHistory;
        Closed += (_, _) => _host.History.Changed -= RefreshHistory;

        _host.StateChanged += Refresh;
        Closed += (_, _) => _host.StateChanged -= Refresh;
        Activated += (_, _) =>
        {
            RefreshVoices();
            Refresh();
        };

        RefreshVoices();
        Refresh();
        RefreshHistory();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_host.IsExiting && _host.Settings.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_host.Settings.Current.TrayHintShown)
            {
                _host.Settings.Update(s => s.TrayHintShown = true);
                _host.NotifyMinimizedToTray();
            }

            return;
        }

        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ refresh

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var settings = _host.Settings.Current;
            var active = _host.CaptureActive;

            CaptureToggle.IsChecked = active;
            StatusLabel.Text = active ? "Activo" : "En pausa";
            StatusLabel.Foreground = active ? (Brush)FindResource("Text1") : (Brush)FindResource("Text2");
            StatusDot.Fill = active ? (Brush)FindResource("Success") : (Brush)FindResource("Text3");
            HeroTitle.Text = active ? "Reveal está activo" : "Reveal está en pausa";
            HeroTitle.Foreground = active ? (Brush)FindResource("Text1") : (Brush)FindResource("Text2");
            CaptureHint.Text = active
                ? "Selecciona una palabra o frase en cualquier aplicación y verás su traducción al instante."
                : $"Pulsa el botón o {settings.StartHotkey} para empezar a capturar tus selecciones.";

            StartKeyText.Text = settings.StartHotkey;
            StopKeyText.Text = settings.StopHotkey;
            if (_capturing is null)
            {
                // Only problems and notices take space; the general note lives in the chip tooltip.
                ShowHotkeyMessage("Warning", _host.HotkeyProblem ?? _host.HotkeyNotice);
            }

            // document
            var path = settings.VocabularyPath;
            DocPath.Text = string.IsNullOrWhiteSpace(path) ? "Ningún documento seleccionado" : path;
            DocPath.Foreground = (Brush)FindResource(string.IsNullOrWhiteSpace(path) ? "Text3" : "Text1");
            DocPath.ToolTip = path;
            OpenDocButton.IsEnabled = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            if (string.IsNullOrWhiteSpace(path))
            {
                DocStatus.Text = "Elige un .docx existente o crea uno nuevo para poder guardar palabras.";
                DocStatus.Foreground = (Brush)FindResource("Warning");
            }
            else if (!File.Exists(path))
            {
                DocStatus.Text = "No se encuentra el archivo. Elígelo de nuevo.";
                DocStatus.Foreground = (Brush)FindResource("Danger");
            }
            else
            {
                DocStatus.Text = "Listo: las palabras se añaden al final del documento.";
                DocStatus.Foreground = (Brush)FindResource("Text3");
            }

            // translation pack
            if (!_downloading)
            {
                var installed = _host.Models.AllInstalled;
                PackText.Text = installed
                    ? "Instalada · la traducción inglés ↔ español funciona sin Internet y tu texto nunca sale del equipo."
                    : "Reveal traduce en tu equipo con modelos libres (Opus-MT). Hay que descargarlos una vez (≈ 230 MB); después no necesita Internet.";
                PackButton.Content = "Descargar";
                PackButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
                PackButton.IsEnabled = true;
                PackCard.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ------------------------------------------------------------------ recent lookups

    private const int HistoryRows = 4;

    private void RefreshHistory()
    {
        var items = _host.History.Items;
        HistoryList.Children.Clear();
        foreach (var item in items.Take(HistoryRows)) HistoryList.Children.Add(BuildHistoryRow(item));
        HistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryClear.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (items.Count == 0) HistoryMessage.Visibility = Visibility.Collapsed;
    }

    private UIElement BuildHistoryRow(HistoryItem item)
    {
        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

        var texts = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var head = new System.Windows.Controls.TextBlock { Text = item.Headword, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var trans = new System.Windows.Controls.TextBlock { Text = item.Translation, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        trans.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text3");
        texts.Children.Add(head);
        if (item.Translation.Length > 0) texts.Children.Add(trans);
        grid.Children.Add(texts);

        UIElement action;
        if (item.Saved || item.Saving)
        {
            var done = new System.Windows.Controls.TextBlock
            {
                Text = item.Saving ? "Guardando…" : "✓ Guardada", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0),
            };
            done.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, item.Saving ? "Text3" : "Success");
            action = done;
        }
        else
        {
            var save = new System.Windows.Controls.Button
            {
                Content = "Guardar", Padding = new Thickness(11, 5, 11, 5), FontSize = 11.5, Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = "Añadir a tu documento de vocabulario",
            };
            save.SetResourceReference(StyleProperty, "BaseButton");
            save.Click += async (_, _) =>
            {
                var problem = await _host.SaveFromHistoryAsync(item);
                HistoryMessage.Text = problem ?? "";
                HistoryMessage.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            };
            action = save;
        }

        System.Windows.Controls.Grid.SetColumn(action, 1);
        grid.Children.Add(action);
        var row = new System.Windows.Controls.Border
        {
            Child = grid, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 6, 8, 6), Background = Brushes.Transparent,
            ToolTip = item.Result.Example,
        };
        row.MouseEnter += (_, _) => row.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Bg2");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    // ------------------------------------------------------------------ document

    public void PromptForDocument()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Elige tu documento de vocabulario",
            Filter = "Documentos de Word (*.docx)|*.docx",
            CheckFileExists = true,
            Multiselect = false,
        };

        var current = _host.Settings.Current.VocabularyPath;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(Path.GetDirectoryName(current)))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
        }
        else
        {
            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // Make sure it really is a readable Word document (open in Word is fine: reading is shared).
            try { OpenXmlVocabularyStore.ReadParagraphs(dialog.FileName); }
            catch (IOException) { /* locked by Word: saving will go through Word */ }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "No se pudo abrir ese archivo como documento de Word.\n\n" + ex.Message, "Reveal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _host.Settings.Update(s => s.VocabularyPath = dialog.FileName);
        Refresh();
    }

    private void CreateDocument()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Crear un nuevo documento de vocabulario",
            Filter = "Documentos de Word (*.docx)|*.docx",
            FileName = "Vocabulary.docx",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            if (File.Exists(dialog.FileName))
            {
                // Never destroy an existing document: an existing file is simply selected.
                _host.Settings.Update(s => s.VocabularyPath = dialog.FileName);
            }
            else
            {
                VocabularyService.CreateDocument(dialog.FileName);
                _host.Settings.Update(s => s.VocabularyPath = dialog.FileName);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "No se pudo crear el documento.\n\n" + ex.Message, "Reveal", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Refresh();
    }

    private void OpenDocument()
    {
        var path = _host.Settings.Current.VocabularyPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Reveal"); }
    }

    // ------------------------------------------------------------------ voices

    private void RefreshVoices()
    {
        var voices = VoiceCatalog.GetEnglishVoices();
        FillCombo(AmericanCombo, voices, EnglishVariant.American, _host.Settings.Current.AmericanVoiceId);
        FillCombo(BritishCombo, voices, EnglishVariant.British, _host.Settings.Current.BritishVoiceId);

        var missing = new List<string>();
        if (VoiceCatalog.ForVariant(voices, EnglishVariant.American).Count == 0) missing.Add("americana (en-US)");
        if (VoiceCatalog.ForVariant(voices, EnglishVariant.British).Count == 0) missing.Add("británica (en-GB)");

        VoiceHelp.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VoiceHelpText.Text = missing.Count > 0
            ? $"Windows no tiene instalada ninguna voz {string.Join(" ni ", missing)}. Añádela en Configuración → Hora e idioma → Voz → «Agregar voces»; Reveal la usará automáticamente."
            : "";
    }

    private void FillCombo(System.Windows.Controls.ComboBox combo, IReadOnlyList<VoiceInfo> all, EnglishVariant variant, string? selectedId)
    {
        _refreshing = true;
        try
        {
            var voices = VoiceCatalog.ForVariant(all, variant);
            combo.Items.Clear();
            if (voices.Count == 0)
            {
                combo.Items.Add(new VoiceChoice(null, "No hay voces instaladas"));
                combo.SelectedIndex = 0;
                combo.IsEnabled = false;
                return;
            }

            combo.IsEnabled = true;
            combo.Items.Add(new VoiceChoice(null, "Automática (la mejor disponible)"));
            foreach (var v in voices) combo.Items.Add(new VoiceChoice(v.Id, v.Display));
            var selected = combo.Items.OfType<VoiceChoice>().FirstOrDefault(c => c.Id == selectedId) ?? (VoiceChoice)combo.Items[0];
            combo.SelectedItem = selected;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnVoiceChanged(EnglishVariant variant, System.Windows.Controls.ComboBox combo)
    {
        if (_refreshing || combo.SelectedItem is not VoiceChoice choice || !combo.IsEnabled) return;
        _host.Settings.Update(s =>
        {
            if (variant == EnglishVariant.American) s.AmericanVoiceId = choice.Id;
            else s.BritishVoiceId = choice.Id;
        });
    }

    private async Task TestVoiceAsync(EnglishVariant variant)
    {
        var voices = VoiceCatalog.GetEnglishVoices();
        var preferred = variant == EnglishVariant.American ? _host.Settings.Current.AmericanVoiceId : _host.Settings.Current.BritishVoiceId;
        var voice = VoiceCatalog.Resolve(voices, variant, preferred);
        if (voice is null)
        {
            VoiceHelp.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            _host.Speech.Rate = _host.Settings.Current.SpeechRate;
            await _host.Speech.SpeakAsync(variant == EnglishVariant.American ? "Reveal. Hello, this is American English." : "Reveal. Hello, this is British English.", voice);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "No se pudo reproducir la voz:\n" + ex.Message, "Reveal");
        }
    }

    // ------------------------------------------------------------------ hotkeys

    private void BeginHotkeyCapture(string which)
    {
        _capturing = which;
        ShowHotkeyMessage("Accent", "Pulsa la nueva combinación (por ejemplo Ctrl+Alt+R). Esc para cancelar.");
        (which == "start" ? StartKeyText : StopKeyText).Text = "…";
        Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is null) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
            or Key.None or Key.DeadCharProcessed or Key.ImeProcessed)
        {
            return; // wait for the actual key
        }

        var which = _capturing;
        _capturing = null;

        if (key == Key.Escape)
        {
            Refresh();
            return;
        }

        var gesture = HotkeyGesture.FromKey(key, Keyboard.Modifiers);
        if (!gesture.IsValid)
        {
            ShowHotkeyMessage("Warning", "El atajo debe incluir al menos Ctrl, Alt, Shift o Win.");
            StartKeyText.Text = _host.Settings.Current.StartHotkey;
            StopKeyText.Text = _host.Settings.Current.StopHotkey;
            return;
        }

        var start = which == "start" ? gesture.ToString() : _host.Settings.Current.StartHotkey;
        var stop = which == "stop" ? gesture.ToString() : _host.Settings.Current.StopHotkey;
        if (string.Equals(start, stop, StringComparison.OrdinalIgnoreCase))
        {
            Refresh();
            ShowHotkeyMessage("Warning", "Los dos atajos tienen que ser distintos.");
            return;
        }

        var problem = _host.TrySetHotkeys(start, stop);
        Refresh();
        ShowHotkeyMessage(problem is null ? "Success" : "Warning", problem ?? $"Atajo actualizado: {gesture}");
    }

    private void ShowHotkeyMessage(string brushKey, string? text)
    {
        HotkeyMessage.Foreground = (Brush)FindResource(brushKey);
        HotkeyMessage.Text = text ?? "";
        HotkeyMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ offline pack

    private async Task DownloadPackAsync()
    {
        if (_downloading) return;
        _downloading = true;
        _downloadCts = new CancellationTokenSource();
        PackButton.IsEnabled = false;
        PackButton.Content = "Descargando…";
        PackProgress.Visibility = Visibility.Visible;
        PackProgress.Value = 0;
        PackText.Text = "Descargando modelos de traducción (Hugging Face, una sola vez)…";

        try
        {
            var packs = ModelPack.All;
            var progress = new Progress<DownloadProgress>(p =>
            {
                var index = packs.ToList().FindIndex(x => x.Id == p.Pack);
                var fileFraction = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0;
                var fileWeight = p.File.EndsWith(".onnx") ? 1.0 : 0.02;
                PackProgress.Value = Math.Min(1, (index + (p.File.EndsWith(".onnx") ? fileFraction * 0.95 : 0)) / packs.Count);
                PackText.Text = $"Descargando {p.File} ({p.BytesDone / 1_048_576} MB)…";
                _ = fileWeight;
            });
            await _host.Models.DownloadAllAsync(progress, _downloadCts.Token);
            PackProgress.Value = 1;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Model download failed", ex);
            PackText.Text = "No se pudo descargar: " + ex.Message;
        }
        finally
        {
            _downloading = false;
            PackProgress.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }
}

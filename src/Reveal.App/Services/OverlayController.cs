using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Reveal.App.Views;
using Reveal.Core.Lookup;
using Reveal.Core.Models;
using Reveal.Core.Settings;
using Reveal.Core.Text;
using Reveal.Core.Vocabulary;
using Reveal.Platform.Capture;
using Reveal.Platform.Speech;

namespace Reveal.App.Services;

/// <summary>
/// Coordinates what happens after text is captured: lookup -> card -> pronunciation -> save.
/// All members must be used from the UI thread.
/// </summary>
internal sealed class OverlayController
{
    private readonly Dispatcher _dispatcher;
    private readonly OverlayWindow _overlay;
    private readonly ToastWindow _toast;
    private readonly LookupService _lookup;
    private readonly VocabularyService _vocabulary;
    private readonly SpeechPlayer _speech;
    private readonly SettingsStore _settings;
    private readonly Action _openDocumentPicker;
    private readonly LookupHistory _history;
    private readonly DispatcherTimer _autoClose = new();

    private CancellationTokenSource? _lookupCts;
    private CancellationTokenSource? _speechCts;
    private CapturedSelection? _current;
    private Rect _anchor;
    private LookupResult? _result;
    private Lang? _forcedLanguage;
    private LanguageGuess? _guess;
    private IReadOnlyList<VoiceInfo> _voices = Array.Empty<VoiceInfo>();
    private readonly HashSet<EnglishVariant> _voiceHintShown = new();
    private DateTime _lastFailureToast = DateTime.MinValue;

    public OverlayController(
        Dispatcher dispatcher, OverlayWindow overlay, ToastWindow toast, LookupService lookup,
        VocabularyService vocabulary, SpeechPlayer speech, SettingsStore settings, Action openDocumentPicker, LookupHistory history)
    {
        _history = history;
        _dispatcher = dispatcher;
        _overlay = overlay;
        _toast = toast;
        _lookup = lookup;
        _vocabulary = vocabulary;
        _speech = speech;
        _settings = settings;
        _openDocumentPicker = openDocumentPicker;

        _overlay.SpeakRequested += v => _ = SpeakAsync(v);
        _overlay.SaveRequested += () => _ = SaveAsync();
        _overlay.CloseRequested += Dismiss;
        _overlay.SwapRequested += () => _ = SwapLanguageAsync();

        _autoClose.Tick += (_, _) =>
        {
            if (_overlay.IsMouseOver) return; // keep it while the user is reading / interacting
            Dismiss();
        };

        RefreshVoices();
    }

    public bool IsVisible => _overlay.IsShown;

    public bool ContainsScreenPoint(int x, int y) => _overlay.ContainsScreenPoint(x, y);

    public void RefreshVoices() => _voices = VoiceCatalog.GetEnglishVoices();

    // ------------------------------------------------------------------ show

    public void OnSelectionCaptured(CapturedSelection selection) => _dispatcher.BeginInvoke(() => _ = ShowAsync(selection, null));

    public void OnCaptureFailed(CaptureFailure failure) => _dispatcher.BeginInvoke(() =>
    {
        // Terminals / unsupported apps would otherwise nag on every click: one hint every 20 s at most.
        if ((DateTime.UtcNow - _lastFailureToast).TotalSeconds < 20) return;
        _lastFailureToast = DateTime.UtcNow;

        var (title, subtitle) = failure.Reason switch
        {
            CaptureFailureReason.TooLong => ("Selección demasiado larga", "Selecciona una palabra o una frase corta."),
            CaptureFailureReason.NothingCopied => ("No se pudo leer la selección", "Esta aplicación no permite que Reveal vea el texto seleccionado."),
            _ => ("No se pudo leer la selección", "Esta aplicación solo permite leerla mediante accesibilidad."),
        };
        _toast.ShowToast(title, subtitle, (System.Windows.Media.Brush)_toast.FindResource("Warning"), 2600);
    });

    private async Task ShowAsync(CapturedSelection selection, Lang? forced)
    {
        var cleaned = TextNormalizer.CleanSelection(selection.Text);
        if (cleaned.Length == 0 || !cleaned.Any(char.IsLetter)) return;

        // The same word selected again while its card is showing: nothing to do.
        if (forced is null && _result is not null && IsVisible && _current is not null &&
            TextNormalizer.CleanSelection(_current.Text) == cleaned) return;

        _lookupCts?.Cancel();
        var cts = _lookupCts = new CancellationTokenSource();
        _speech.Stop();
        _current = selection;
        _forcedLanguage = forced;
        _anchor = ComputeAnchor(selection);
        _guess = forced is null ? _lookup.Detect(cleaned) : null;

        var sw = Stopwatch.StartNew();
        try
        {
            var final = await _lookup.LookupAsync(selection.Text, forced, partial =>
            {
                _dispatcher.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    ShowResult(partial, present: true);
                });
            }, cts.Token);

            if (cts.IsCancellationRequested) return;
            Log.Info($"lookup {final.SourceLanguage}->{final.Status} in {sw.ElapsedMilliseconds} ms via {selection.Source}/{selection.ProcessName} engine={final.TranslationEngine}");
            if (final.Status == LookupStatus.Failed && string.IsNullOrWhiteSpace(final.Headword) && final.SourceText.Length == 0) return;
            ShowResult(final, present: true);
            _history.Add(final);
            _ = AfterResultAsync(final, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer selection
        }
        catch (Exception ex)
        {
            Log.Error("Lookup failed", ex);
            _toast.ShowToast("Algo ha fallado", ex.Message, (System.Windows.Media.Brush)_toast.FindResource("Danger"), 3000);
        }
    }

    private void ShowResult(LookupResult result, bool present)
    {
        _result = result;
        var american = VoiceCatalog.Resolve(_voices, EnglishVariant.American, _settings.Current.AmericanVoiceId) is not null;
        var british = VoiceCatalog.Resolve(_voices, EnglishVariant.British, _settings.Current.BritishVoiceId) is not null;

        var canSwap = _guess?.Ambiguous == true && result.Status != LookupStatus.Failed;
        var swapLabel = result.SourceLanguage == Lang.Spanish ? "Tratar como inglés" : "Tratar como español";
        _overlay.ApplyResult(result, american, british, canSwap, swapLabel);
        if (present) _overlay.Present(_anchor);
        RestartAutoClose();
    }

    /// <summary>After the final result: is it already in the vocabulary? warm up the audio.</summary>
    private async Task AfterResultAsync(LookupResult result, CancellationToken ct)
    {
        if (result.Status == LookupStatus.Failed || result.Headword.Length == 0) return;

        var preload = new List<Task>();
        foreach (var variant in new[] { EnglishVariant.American, EnglishVariant.British })
        {
            var voice = VoiceCatalog.Resolve(_voices, variant, variant == EnglishVariant.American ? _settings.Current.AmericanVoiceId : _settings.Current.BritishVoiceId);
            if (voice is not null) preload.Add(_speech.PrepareAsync(result.Headword, voice));
        }

        try
        {
            var duplicate = await _vocabulary.FindAsync(result.Headword, ct);
            if (ct.IsCancellationRequested || duplicate is null) return;
            await _dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                var message = duplicate.Kind == DuplicateKind.Entry
                    ? "Ya está en tu vocabulario"
                    : $"Sinónimo de {duplicate.Owner} en tu vocabulario";
                _overlay.SetSaveState(SaveUiState.AlreadySaved, message);
                _history.MarkSaved(result.Headword);
            });
        }
        catch (OperationCanceledException)
        {
        }

        await Task.WhenAll(preload);
    }

    private Rect ComputeAnchor(CapturedSelection s)
    {
        var cursor = new Rect(s.CursorX - 2, s.CursorY - 2, 4, 22);
        if (s.BoundsPx is { } b && b.Width > 0 && b.Height > 0 && b.Height <= 80 && b.Width <= 900)
        {
            return b;
        }

        return cursor;
    }

    // ------------------------------------------------------------------ actions

    private async Task SpeakAsync(EnglishVariant variant)
    {
        var result = _result;
        if (result is null || result.Headword.Length == 0) return;

        var preferred = variant == EnglishVariant.American ? _settings.Current.AmericanVoiceId : _settings.Current.BritishVoiceId;
        var voice = VoiceCatalog.Resolve(_voices, variant, preferred);
        if (voice is null)
        {
            // Second click on a missing voice opens Windows' voice settings.
            if (!_voiceHintShown.Add(variant))
            {
                Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true });
                return;
            }

            var name = variant == EnglishVariant.American ? "americana (en-US)" : "británica (en-GB)";
            _overlay.SetStatus($"No hay voz {name} instalada. Pulsa otra vez para abrir los ajustes de voz de Windows.", OverlayWindow.StatusKind.Warning);
            return;
        }

        _speechCts?.Cancel();
        var cts = _speechCts = new CancellationTokenSource();
        _speech.Rate = _settings.Current.SpeechRate;
        _overlay.SetPlaying(variant);
        try
        {
            var sw = Stopwatch.StartNew();
            await _speech.SpeakAsync(result.Headword, voice, cts.Token);
            Log.Info($"spoke {variant} with '{voice.Name}' ({voice.Language}) in {sw.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Speech failed", ex);
            _overlay.SetStatus("No se pudo reproducir la voz: " + ex.Message, OverlayWindow.StatusKind.Danger);
        }
        finally
        {
            if (!cts.IsCancellationRequested || _speechCts == cts) _overlay.SetPlaying(null);
        }
    }

    private async Task SaveAsync()
    {
        var r = _result;
        if (r is null || r.Headword.Length == 0) return;

        if (string.IsNullOrWhiteSpace(_vocabulary.DocumentPath))
        {
            _openDocumentPicker();
            _overlay.SetSaveState(SaveUiState.NeedsDocument, "Elige un documento de vocabulario (.docx)");
            return;
        }

        _overlay.SetSaveState(SaveUiState.Saving, null);
        var entry = new VocabularyEntry(r.Headword, r.Translations, r.Example, r.Synonyms);
        var result = await _vocabulary.SaveAsync(entry);
        Log.Info($"save -> {result.Outcome}{(result.ViaWord ? " (via Word)" : "")}");

        switch (result.Outcome)
        {
            case SaveOutcome.Added:
                _overlay.SetSaveState(SaveUiState.Saved, result.ViaWord ? "Añadida en Word" : "Guardada en tu vocabulario");
                _history.MarkSaved(r.Headword);
                break;
            case SaveOutcome.Duplicate:
                _overlay.SetSaveState(SaveUiState.AlreadySaved, result.Message.TrimEnd('.'));
                _history.MarkSaved(r.Headword);
                break;
            case SaveOutcome.NoDocumentConfigured:
            case SaveOutcome.DocumentNotFound:
                _overlay.SetSaveState(SaveUiState.NeedsDocument, result.Message);
                break;
            default:
                _overlay.SetSaveState(SaveUiState.Error, result.Message);
                break;
        }
    }

    private async Task SwapLanguageAsync()
    {
        if (_current is null || _result is null) return;
        var next = _result.SourceLanguage == Lang.Spanish ? Lang.English : Lang.Spanish;
        await ShowAsync(_current, next);
    }

    // ------------------------------------------------------------------ dismiss

    public void Dismiss()
    {
        _lookupCts?.Cancel();
        _speechCts?.Cancel();
        _speech.Stop();
        _autoClose.Stop();
        _overlay.HideOverlay();
        _result = null;
        _current = null;
    }

    public void OnPointerPressedOutside() => _dispatcher.BeginInvoke(() =>
    {
        if (IsVisible) Dismiss();
    });

    public void OnEscape() => _dispatcher.BeginInvoke(() =>
    {
        if (IsVisible) Dismiss();
    });

    private void RestartAutoClose()
    {
        _autoClose.Stop();
        var seconds = _settings.Current.OverlayAutoCloseSeconds;
        if (seconds <= 0) return;
        _autoClose.Interval = TimeSpan.FromSeconds(seconds);
        _autoClose.Start();
    }
}

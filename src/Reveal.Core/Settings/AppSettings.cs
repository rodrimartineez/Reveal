namespace Reveal.Core.Settings;

/// <summary>User configuration persisted as JSON. Never contains any captured text.</summary>
public sealed class AppSettings
{
    public string? VocabularyPath { get; set; }

    public string StartHotkey { get; set; } = "Ctrl+Shift+T";

    public string StopHotkey { get; set; } = "Ctrl+Shift+X";

    /// <summary>Windows voice id (as reported by the voice catalog); null = best match.</summary>
    public string? AmericanVoiceId { get; set; }

    public string? BritishVoiceId { get; set; }

    public bool StartWithWindows { get; set; }

    public bool StartCaptureOnLaunch { get; set; }

    public bool CloseToTray { get; set; } = true;

    /// <summary>Also react to keyboard selections (Shift+arrows...).</summary>
    public bool CaptureKeyboardSelection { get; set; } = true;

    /// <summary>When an app doesn't expose the selection through UI Automation, copy it with Ctrl+C (clipboard is restored).</summary>
    public bool UseClipboardFallback { get; set; } = true;

    /// <summary>
    /// Use a free online translation endpoint when the offline engine isn't available.
    /// Off by default: it would send the selected text over the Internet.
    /// </summary>
    public bool AllowOnlineFallback { get; set; }

    /// <summary>0 = keep the overlay until the user clicks elsewhere / presses Esc.</summary>
    public int OverlayAutoCloseSeconds { get; set; }

    public bool ShowPronunciationHints { get; set; } = true;

    public bool FirstRunCompleted { get; set; }

    public bool TrayHintShown { get; set; }

    public double SpeechRate { get; set; } = 1.0;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

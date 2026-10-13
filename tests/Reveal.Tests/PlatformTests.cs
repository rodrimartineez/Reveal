using System.Windows;
using Reveal.Platform.Capture;
using Reveal.Platform.Hotkeys;
using Reveal.Platform.Speech;
using Reveal.Platform.SystemIntegration;
using Xunit.Abstractions;

namespace Reveal.Tests;

public sealed class PlatformTests(ITestOutputHelper output)
{
    // ---------- overlay placement (any monitor layout / scaling) ----------

    [Fact]
    public void Placement_goes_below_the_selection_when_there_is_room()
    {
        var work = new Rect(0, 0, 1920, 1020);
        var p = OverlayPlacement.Place(new Rect(300, 200, 100, 24), 425, 300, work, 12);
        Assert.Equal(new Point(300, 236), p);
    }

    [Fact]
    public void Placement_flips_above_near_the_bottom_edge()
    {
        var work = new Rect(0, 0, 1920, 1020);
        var p = OverlayPlacement.Place(new Rect(300, 900, 100, 24), 425, 300, work, 12);
        Assert.Equal(900 - 12 - 300, p.Y);
        Assert.True(p.Y >= work.Top);
    }

    [Fact]
    public void Placement_is_clamped_to_the_right_edge()
    {
        var work = new Rect(0, 0, 1920, 1020);
        var p = OverlayPlacement.Place(new Rect(1850, 200, 60, 24), 425, 300, work, 12);
        Assert.Equal(1920 - 425, p.X);
    }

    [Fact]
    public void Placement_works_on_monitors_with_negative_coordinates_and_high_scaling()
    {
        // Secondary monitor to the left of the primary, 150 % scaling: the card is 1.5x bigger in pixels.
        var work = new Rect(-1920, -200, 1920, 1032);
        var scale = 1.5;
        var p = OverlayPlacement.Place(new Rect(-1000, 100, 80, 24), 340 * scale, 260 * scale, work, 10 * scale);
        Assert.True(p.X >= work.Left && p.X + 340 * scale <= work.Right);
        Assert.True(p.Y >= work.Top && p.Y + 260 * scale <= work.Bottom);
    }

    [Fact]
    public void Placement_pins_to_the_work_area_when_nothing_fits()
    {
        var work = new Rect(0, 0, 800, 300);
        var p = OverlayPlacement.Place(new Rect(100, 150, 50, 20), 400, 280, work, 10);
        Assert.True(p.Y >= 0 && p.Y + 280 <= 300);
    }

    [Fact]
    public void Monitor_geometry_reports_a_sane_primary_monitor()
    {
        var m = MonitorGeometry.FromPoint(10, 10);
        output.WriteLine($"{m.Bounds} work={m.WorkArea} scale={m.ScaleX}");
        Assert.True(m.Bounds.Width >= 640 && m.ScaleX >= 1.0 && m.WorkArea.Height > 0);
    }

    // ---------- hotkeys ----------

    [Theory]
    [InlineData("Ctrl+Shift+T", true)]
    [InlineData("ctrl + shift + x", true)]
    [InlineData("Alt+F9", true)]
    [InlineData("Win+Shift+R", true)]
    [InlineData("Ctrl+1", true)]
    [InlineData("T", false)]
    [InlineData("Ctrl+Shift", false)]
    [InlineData("Ctrl+Banana", false)]
    [InlineData("", false)]
    public void Hotkey_parsing(string text, bool valid)
    {
        Assert.Equal(valid, HotkeyGesture.TryParse(text, out var g));
        if (valid) Assert.True(g.IsValid);
    }

    [Fact]
    public void Hotkey_round_trips_through_text()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+T", out var g));
        Assert.Equal("Ctrl+Shift+T", g.ToString());
        Assert.True(HotkeyGesture.TryParse("Shift+Alt+Ctrl+F5", out var h));
        Assert.Equal("Ctrl+Alt+Shift+F5", h.ToString());
    }

    // ---------- speech ----------

    [Fact]
    public void Voices_are_listed_and_american_resolves()
    {
        var voices = VoiceCatalog.GetEnglishVoices();
        foreach (var v in voices) output.WriteLine($"{v.Id} | {v.Name} | {v.Language}");
        if (voices.Count == 0) return; // no TTS on this machine

        var american = VoiceCatalog.Resolve(voices, EnglishVariant.American, null);
        Assert.NotNull(american);
        Assert.True(american!.IsAmerican);
        // The configured voice wins when it exists, and a stale id falls back to the best match.
        Assert.Equal(american.Id, VoiceCatalog.Resolve(voices, EnglishVariant.American, american.Id)!.Id);
        Assert.Equal(american.Id, VoiceCatalog.Resolve(voices, EnglishVariant.American, "no-such-voice")!.Id);
    }

    [Fact]
    public void British_resolution_is_null_without_a_british_voice_and_exact_with_one()
    {
        var us = new VoiceInfo("us1", "Zira", "en-US", "Female");
        var gb = new VoiceInfo("gb1", "Hazel", "en-GB", "Female");
        Assert.Null(VoiceCatalog.Resolve([us], EnglishVariant.British, null));
        Assert.Equal("gb1", VoiceCatalog.Resolve([us, gb], EnglishVariant.British, null)!.Id);
        Assert.Equal("us1", VoiceCatalog.Resolve([us, gb], EnglishVariant.American, null)!.Id);
    }

    [Fact]
    public async Task Speech_synthesises_and_plays_audio_with_the_american_voice()
    {
        var voices = VoiceCatalog.GetEnglishVoices();
        var voice = VoiceCatalog.Resolve(voices, EnglishVariant.American, null);
        if (voice is null) return;

        using var player = new SpeechPlayer();
        var started = false;
        player.Started += () => started = true;
        await player.PrepareAsync("reluctant", voice);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await player.SpeakAsync("reluctant", voice);
        output.WriteLine($"spoke 'reluctant' with {voice.Name} in {sw.ElapsedMilliseconds} ms");
        Assert.True(started);
        Assert.InRange(sw.ElapsedMilliseconds, 300, 8000); // real audio took real time to play
    }

    // ---------- clipboard snapshot / restore ----------

    [Fact]
    public void Clipboard_snapshot_restores_user_content_after_a_temporary_copy()
    {
        var original = ClipboardAccess.TakeSnapshot();
        Assert.NotNull(original);
        try
        {
            RunSta(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, "user clipboard text"), true));
            var snapshot = ClipboardAccess.TakeSnapshot();
            Assert.NotNull(snapshot);
            Assert.False(snapshot!.IsEmpty);

            // Simulate the target app copying the selection ...
            RunSta(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, "selected word"), true));
            Assert.Equal("selected word", ClipboardAccess.ReadText());

            // ... and Reveal putting the user's content back.
            Assert.True(ClipboardAccess.Restore(snapshot));
            Assert.Equal("user clipboard text", ClipboardAccess.ReadText());
        }
        finally
        {
            if (original!.IsEmpty) ClipboardAccess.Clear();
            else ClipboardAccess.Restore(original);
        }
    }

    [Fact]
    public void Clipboard_snapshot_keeps_multiple_formats()
    {
        var original = ClipboardAccess.TakeSnapshot();
        try
        {
            RunSta(() =>
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, "plain");
                data.SetData(DataFormats.Html, "<b>rich</b>");
                data.SetData(DataFormats.Rtf, "{\\rtf1 rich}");
                Clipboard.SetDataObject(data, true);
            });
            var snapshot = ClipboardAccess.TakeSnapshot()!;
            Assert.True(snapshot.Items.Count >= 3);

            RunSta(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, "other"), true));
            ClipboardAccess.Restore(snapshot);

            RunSta(() =>
            {
                Assert.Equal("plain", Clipboard.GetText());
                Assert.True(Clipboard.ContainsText(TextDataFormat.Html));
                Assert.True(Clipboard.ContainsText(TextDataFormat.Rtf));
            });
        }
        finally
        {
            if (original!.IsEmpty) ClipboardAccess.Clear();
            else ClipboardAccess.Restore(original);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception("STA action failed", error);
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using Interop.UIAutomationClient;

namespace Reveal.Platform.Capture;

public enum UiaStatus
{
    /// <summary>The focused control doesn't expose a text pattern (can't tell what is selected).</summary>
    NoTextPattern,

    /// <summary>The control exposes text and nothing is selected.</summary>
    NoSelection,

    /// <summary>A selection was read.</summary>
    Text,

    /// <summary>Password field: never read.</summary>
    Password,

    /// <summary>UI Automation failed or timed out.</summary>
    Error,
}

public sealed record UiaSelection(UiaStatus Status, string Text = "", Rect? Bounds = null);

/// <summary>
/// Reads the current selection through native Microsoft UI Automation (IUIAutomation, no clipboard involved).
/// Every call runs on a short-lived MTA thread with a timeout: a hung target application can never block Reveal.
/// </summary>
internal static class UiaSelectionReader
{
    public const int MaxChars = 1200;

    private const int UiaTextPatternId = 10014;

    private static IUIAutomation? _automation;
    private static readonly object Gate = new();

    private static IUIAutomation Automation
    {
        get
        {
            lock (Gate) return _automation ??= new CUIAutomation8();
        }
    }

    public static UiaSelection Read(int mouseX, int mouseY, TimeSpan timeout)
    {
        UiaSelection? result = null;
        var worker = new Thread(() =>
        {
            try { result = ReadCore(mouseX, mouseY); }
            catch (Exception) { result = new UiaSelection(UiaStatus.Error); }
        })
        { IsBackground = true, Name = "Reveal.UIA" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();

        return worker.Join(timeout) ? result ?? new UiaSelection(UiaStatus.Error) : new UiaSelection(UiaStatus.Error);
    }

    private static UiaSelection ReadCore(int x, int y)
    {
        var automation = Automation;

        IUIAutomationElement? focused = null;
        try { focused = automation.GetFocusedElement(); } catch (COMException) { /* nothing focused */ }

        if (focused is not null)
        {
            if (IsPassword(focused)) return new UiaSelection(UiaStatus.Password);

            var fromFocus = TryReadFrom(automation, focused);
            if (fromFocus is not null) return fromFocus;
        }

        // Some hosts (browsers, Electron) report a container as focused; ask what is under the mouse instead.
        try
        {
            var atPoint = automation.ElementFromPoint(new tagPOINT { x = x, y = y });
            if (atPoint is not null)
            {
                if (IsPassword(atPoint)) return new UiaSelection(UiaStatus.Password);
                var fromPoint = TryReadFrom(automation, atPoint);
                if (fromPoint is not null) return fromPoint;
            }
        }
        catch (COMException)
        {
            // fall through
        }

        return new UiaSelection(UiaStatus.NoTextPattern);
    }

    private static bool IsPassword(IUIAutomationElement element)
    {
        try { return element.CurrentIsPassword != 0; }
        catch (COMException) { return false; }
    }

    /// <summary>Tries the element and a few ancestors; null = none of them exposes a text pattern.</summary>
    private static UiaSelection? TryReadFrom(IUIAutomation automation, IUIAutomationElement start)
    {
        IUIAutomationElement? current = start;
        for (var depth = 0; depth < 4 && current is not null; depth++)
        {
            IUIAutomationTextPattern? pattern = null;
            try { pattern = current.GetCurrentPattern(UiaTextPatternId) as IUIAutomationTextPattern; }
            catch (COMException) { /* pattern not supported */ }
            catch (InvalidCastException) { }

            if (pattern is not null) return ReadPattern(pattern);

            try { current = automation.ControlViewWalker.GetParentElement(current); }
            catch (COMException) { break; }
        }

        return null;
    }

    /// <summary>
    /// If the selection starts or ends in the middle of a word ("give up smok|"), widen it to whole words.
    /// Falls back to the original range if the control doesn't support the needed operations.
    /// </summary>
    private static IUIAutomationTextRange SnapToWords(IUIAutomationTextRange range)
    {
        try
        {
            var probe = range.GetText(MaxChars + 1);
            if (string.IsNullOrEmpty(probe) || probe.Length > 200) return range;

            var start = range.Clone();
            start.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, start, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
            start.ExpandToEnclosingUnit(TextUnit.TextUnit_Word);

            var end = range.Clone();
            end.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, end, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
            end.Move(TextUnit.TextUnit_Character, -1);
            end.ExpandToEnclosingUnit(TextUnit.TextUnit_Word);

            var snapped = range.Clone();
            snapped.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, start, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
            snapped.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, end, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);

            var text = snapped.GetText(MaxChars + 1);
            // Sanity: the snapped text must still contain what the user selected, and not balloon.
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= probe.Length + 60 && text.Contains(probe.Trim(), StringComparison.Ordinal))
            {
                return snapped;
            }
        }
        catch (Exception)
        {
            // controls without word navigation: keep the raw selection
        }

        return range;
    }

    private static UiaSelection ReadPattern(IUIAutomationTextPattern pattern)
    {
        IUIAutomationTextRangeArray ranges;
        try { ranges = pattern.GetSelection(); }
        catch (COMException) { return new UiaSelection(UiaStatus.Error); }

        if (ranges is null || ranges.Length == 0) return new UiaSelection(UiaStatus.NoSelection);

        var parts = new List<string>();
        Rect? bounds = null;
        for (var i = 0; i < ranges.Length; i++)
        {
            var range = ranges.GetElement(i);
            range = SnapToWords(range);
            string text;
            try { text = range.GetText(MaxChars + 1) ?? ""; }
            catch (COMException) { continue; }

            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);

            try
            {
                if (range.GetBoundingRectangles() is double[] rects)
                {
                    for (var k = 0; k + 3 < rects.Length; k += 4)
                    {
                        var r = new Rect(rects[k], rects[k + 1], rects[k + 2], rects[k + 3]);
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        bounds = bounds is null ? r : Rect.Union(bounds.Value, r);
                    }
                }
            }
            catch (COMException)
            {
                // bounds are optional
            }
        }

        var joined = string.Join(" ", parts);
        return string.IsNullOrWhiteSpace(joined)
            ? new UiaSelection(UiaStatus.NoSelection)
            : new UiaSelection(UiaStatus.Text, joined, bounds);
    }
}

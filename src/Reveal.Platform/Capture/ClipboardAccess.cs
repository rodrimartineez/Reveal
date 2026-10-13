using System.Runtime.InteropServices;
using System.Text;
using Reveal.Platform.Native;

namespace Reveal.Platform.Capture;

/// <summary>Raw clipboard helpers (Win32) used by the Ctrl+C fallback.</summary>
internal static class ClipboardAccess
{
    private const long MaxSnapshotBytes = 32 * 1024 * 1024;

    private static bool TryOpen(int attempts = 12, int delayMs = 8)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(delayMs);
        }

        return false;
    }

    public static uint SequenceNumber => NativeMethods.GetClipboardSequenceNumber();

    /// <summary>Reads the Unicode text on the clipboard, or null.</summary>
    public static string? ReadText()
    {
        if (!TryOpen()) return null;
        try
        {
            var handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            var ptr = NativeMethods.GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public sealed class Snapshot
    {
        public List<(uint Format, byte[] Data)> Items { get; } = new();

        public bool IsEmpty => Items.Count == 0;
    }

    private static bool IsSkippedFormat(uint format)
    {
        switch (format)
        {
            case NativeMethods.CF_BITMAP:
            case NativeMethods.CF_PALETTE:
            case NativeMethods.CF_METAFILEPICT:
            case NativeMethods.CF_ENHMETAFILE:
            case NativeMethods.CF_OWNERDISPLAY:
            case NativeMethods.CF_DSPTEXT:
            case NativeMethods.CF_DSPBITMAP:
            case NativeMethods.CF_DSPMETAFILEPICT:
            case NativeMethods.CF_DSPENHMETAFILE:
                return true;
        }

        // GDI object formats (0x300..0x3FF) hold handles, not memory.
        return format is >= 0x0300 and <= 0x03FF;
    }

    /// <summary>
    /// Copies every memory-backed format. Returns null when the clipboard can't be read safely
    /// (busy, too large...), in which case the caller must not touch the clipboard at all.
    /// </summary>
    public static Snapshot? TakeSnapshot()
    {
        if (!TryOpen()) return null;
        try
        {
            var snapshot = new Snapshot();
            long total = 0;
            var format = 0u;
            while ((format = NativeMethods.EnumClipboardFormats(format)) != 0)
            {
                if (IsSkippedFormat(format)) continue;

                var handle = NativeMethods.GetClipboardData(format);
                if (handle == IntPtr.Zero) continue;

                var size = (long)(ulong)NativeMethods.GlobalSize(handle);
                if (size <= 0) continue;
                total += size;
                if (total > MaxSnapshotBytes) return null;

                var ptr = NativeMethods.GlobalLock(handle);
                if (ptr == IntPtr.Zero) continue;
                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(ptr, bytes, 0, (int)size);
                    snapshot.Items.Add((format, bytes));
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }
            }

            return snapshot;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>Puts the snapshot back (marked so Windows clipboard history / cloud sync ignore the restore).</summary>
    public static bool Restore(Snapshot snapshot)
    {
        if (!TryOpen()) return false;
        try
        {
            NativeMethods.EmptyClipboard();

            foreach (var (format, data) in snapshot.Items)
            {
                SetBytes(format, data);
            }

            MarkAsPrivate();
            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public static void Clear()
    {
        if (!TryOpen()) return;
        try { NativeMethods.EmptyClipboard(); }
        finally { NativeMethods.CloseClipboard(); }
    }

    private static void SetBytes(uint format, byte[] data)
    {
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero) return;
        var ptr = NativeMethods.GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
            return;
        }

        Marshal.Copy(data, 0, ptr, data.Length);
        NativeMethods.GlobalUnlock(handle);
        if (NativeMethods.SetClipboardData(format, handle) == IntPtr.Zero) NativeMethods.GlobalFree(handle);
    }

    private static void MarkAsPrivate()
    {
        foreach (var name in new[] { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            var format = NativeMethods.RegisterClipboardFormat(name);
            if (format != 0) SetBytes(format, new byte[4]);
        }
    }

    /// <summary>Asks the foreground application to copy its selection (Ctrl+C), tagging the keys as ours.</summary>
    public static void SendCopyKeystroke(UIntPtr marker)
    {
        var ctrlDown = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
        var inputs = new List<NativeMethods.INPUT>();
        if (!ctrlDown) inputs.Add(Key(NativeMethods.VK_CONTROL, up: false, marker));
        inputs.Add(Key('C', up: false, marker));
        inputs.Add(Key('C', up: true, marker));
        if (!ctrlDown) inputs.Add(Key(NativeMethods.VK_CONTROL, up: true, marker));

        NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static NativeMethods.INPUT Key(int vk, bool up, UIntPtr marker) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.INPUTUNION
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = (ushort)vk,
                wScan = (ushort)NativeMethods.MapVirtualKey((uint)vk, 0),
                dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = marker,
            },
        },
    };

    public static string DescribeFormat(uint format)
    {
        var sb = new StringBuilder(64);
        return NativeMethods.GetClipboardFormatName(format, sb, sb.Capacity) > 0 ? sb.ToString() : format.ToString();
    }
}

// Dev-only test target for Reveal: a window with a classic Edit control and a RichEdit control at a fixed place.
// Usage: TextHost.exe <x> <y> [width] [height]   (physical pixels)
using System.Runtime.InteropServices;


static class Entry
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var x = args.Length > 0 ? int.Parse(args[0]) : 100;
        var y = args.Length > 1 ? int.Parse(args[1]) : 100;
        var w = args.Length > 2 ? int.Parse(args[2]) : 900;
        var h = args.Length > 3 ? int.Parse(args[3]) : 420;

        var form = new Form { Text = "RevealTextHost", StartPosition = FormStartPosition.Manual, Font = new Font("Segoe UI", 14f) };
        var edit = new TextBox
        {
            Multiline = true, Dock = DockStyle.Top, Height = 150,
            Text = "The reluctant student was meticulous about every detail.\r\nShe decided to give up smoking and look forward to a healthier life.\r\nEl niño estaba reacio a aceptar la oferta.\r\nPlease type your pass-word: ",
        };
        var password = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true, Text = "secretpassword" };
        var rich = new RichTextBox { Dock = DockStyle.Fill, Text = "Ubiquitous technology makes life easier. Nevertheless, it can be cumbersome.\nTengo que irme ahora porque llego tarde." };
        var owner = new OwnerDrawn { Dock = DockStyle.Bottom, Height = 46, Name = "OwnerDrawnFallback" };
        form.Controls.Add(rich);
        form.Controls.Add(owner);
        form.Controls.Add(password);
        form.Controls.Add(edit);
        form.TopMost = true;
        form.Load += (_, _) =>
        {
            form.SetBounds(x, y, w, h);
        };
        Application.Run(form);


    }
}

// A control without any accessibility text pattern: forces Reveal's Ctrl+C fallback.
sealed class OwnerDrawn : Control
{
    private readonly string _text = "Fallback control: the reluctant students give up smoking.";
    private int _a = -1, _b = -1;
    private bool _down;
    private readonly Font _font = new("Consolas", 14f);

    public OwnerDrawn()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = true;
        Cursor = Cursors.IBeam;
        BackColor = Color.White;
    }

    private float CharW => TextRenderer.MeasureText("WW", _font, new Size(1000, 100), TextFormatFlags.NoPadding).Width / 2f;

    private int IndexAt(int x) => Math.Clamp((int)Math.Round((x - 6) / CharW), 0, _text.Length);

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); _down = true; _a = _b = IndexAt(e.X); Invalidate(); base.OnMouseDown(e); }

    protected override void OnMouseMove(MouseEventArgs e) { if (_down) { _b = IndexAt(e.X); Invalidate(); } base.OnMouseMove(e); }

    protected override void OnMouseUp(MouseEventArgs e) { _down = false; base.OnMouseUp(e); }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        var i = Math.Min(IndexAt(e.X), _text.Length - 1);
        var s = i; while (s > 0 && char.IsLetter(_text[s - 1])) s--;
        var t = i; while (t < _text.Length && char.IsLetter(_text[t])) t++;
        _a = s; _b = t; Invalidate();
        base.OnMouseDoubleClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "texthost.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " keydown " + e.KeyCode + " ctrl=" + e.Control + " sel=" + _a + "-" + _b + " focused=" + Focused + Environment.NewLine);
        if (e.Control && e.KeyCode == Keys.C && _a != _b)
        {
            var (s, t) = (Math.Min(_a, _b), Math.Max(_a, _b));
            Clipboard.SetText(_text.Substring(s, t - s));
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var (s, t) = (Math.Min(_a, _b), Math.Max(_a, _b));
        if (s >= 0 && t > s)
        {
            e.Graphics.FillRectangle(Brushes.DodgerBlue, 6 + s * CharW, 4, (t - s) * CharW, Height - 8);
        }
        TextRenderer.DrawText(e.Graphics, _text, _font, new Point(6, 8), Color.Black, TextFormatFlags.NoPadding);
    }
}

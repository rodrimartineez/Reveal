using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace Reveal.App.Services;

/// <summary>Notification-area icon: status, quick toggle, open / settings / quit.</summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly Icon _activeIcon;
    private readonly Icon _pausedIcon;

    public event Action? ToggleRequested;

    public event Action? OpenRequested;

    public event Action? SettingsRequested;

    public event Action? ExitRequested;

    public TrayIconService()
    {
        var size = SystemInformation.SmallIconSize;
        _activeIcon = LoadIcon("tray-active.ico", size);
        _pausedIcon = LoadIcon("tray-paused.ico", size);

        _statusItem = new ToolStripMenuItem("Reveal") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Activar captura");
        var open = new ToolStripMenuItem("Abrir Reveal");
        var settings = new ToolStripMenuItem("Ajustes");
        var exit = new ToolStripMenuItem("Salir");

        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke();
        open.Click += (_, _) => OpenRequested?.Invoke();
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            Font = new Font("Segoe UI", 9.5f),
            ShowImageMargin = false,
            Padding = new Padding(4),
            BackColor = Color.FromArgb(21, 23, 27),
            ForeColor = Color.FromArgb(237, 238, 241),
        };
        menu.Items.AddRange([_statusItem, new ToolStripSeparator(), _toggleItem, open, settings, new ToolStripSeparator(), exit]);

        _icon = new NotifyIcon
        {
            Icon = _pausedIcon,
            Text = "Reveal",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    public void SetState(bool active, string startHotkey, string stopHotkey)
    {
        _icon.Icon = active ? _activeIcon : _pausedIcon;
        _icon.Text = active ? $"Reveal — captura activa ({stopHotkey} para parar)" : $"Reveal — en pausa ({startHotkey} para empezar)";
        if (_icon.Text.Length > 63) _icon.Text = _icon.Text[..63];
        _statusItem.Text = active ? "● Captura activa" : "○ Captura en pausa";
        _toggleItem.Text = active ? $"Detener captura   ({stopHotkey})" : $"Iniciar captura   ({startHotkey})";
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(3500);
    }

    private static Icon LoadIcon(string name, System.Drawing.Size size)
    {
        var stream = System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"))!.Stream;
        using (stream) return new Icon(stream, size);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _activeIcon.Dispose();
        _pausedIcon.Dispose();
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(237, 238, 241) : Color.FromArgb(130, 135, 146);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(21, 23, 27);
        private static readonly Color Hover = Color.FromArgb(38, 42, 50);
        private static readonly Color Border = Color.FromArgb(42, 46, 54);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}

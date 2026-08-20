using System;
using System.Drawing;
using System.Windows.Forms;

namespace GlassMusicPlayer.Services;

/// <summary>
/// System tray icon with a context menu for controlling the player.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public event Action? OpenWindow;
    public event Action? PlayPause;
    public event Action? Next;
    public event Action? Prev;
    public event Action? Exit;

    public TrayIconService()
    {
        var menu = new ContextMenuStrip();
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.Renderer = new DarkMenuRenderer();
        menu.BackColor = DarkColor.Bg;
        menu.ForeColor = DarkColor.Text;
        menu.Font = new Font("Segoe UI", 9.5f);
        menu.Padding = new Padding(6, 4, 6, 4);

        AddHeader(menu);
        AddItem(menu, "Открыть плеер", OpenWindow);
        menu.Items.Add(NewSeparator());
        AddItem(menu, "Воспроизведение / Пауза", PlayPause);
        AddItem(menu, "Следующий трек", Next);
        AddItem(menu, "Предыдущий трек", Prev);
        menu.Items.Add(NewSeparator());
        AddItem(menu, "Выход", Exit);

        _notifyIcon = new NotifyIcon
        {
            Icon = IconProvider.LoadTrayIcon(),
            Text = "Glass Music Player",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenWindow?.Invoke();
    }

    private static void AddHeader(ContextMenuStrip menu)
    {
        var header = new ToolStripLabel("GLASS MUSIC PLAYER")
        {
            Padding = new Padding(12, 8, 12, 6),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(156, 138, 255),
            IsLink = false
        };
        menu.Items.Add(header);
        menu.Items.Add(NewSeparator());
    }

    private static ToolStripMenuItem AddItem(ContextMenuStrip menu, string text, Action? action)
    {
        var item = new ToolStripMenuItem(text)
        {
            AutoSize = true,
            Padding = new Padding(12, 6, 12, 6),
            ForeColor = DarkColor.Text,
            BackColor = Color.Transparent
        };
        item.Click += (_, _) => action?.Invoke();
        menu.Items.Add(item);
        return item;
    }

    private static ToolStripSeparator NewSeparator()
    {
        var sep = new ToolStripSeparator
        {
            AutoSize = true,
            Margin = new Padding(6, 2, 6, 2)
        };
        return sep;
    }

    public void SetTrackTitle(string title)
    {
        if (!string.IsNullOrWhiteSpace(title))
            _notifyIcon.Text = title.Length > 60 ? title[..60] : title;
    }

    public void ShowBalloon(string title, string text)
    {
        _notifyIcon.ShowBalloonTip(2000, title, text, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private static class DarkColor
    {
        public static readonly Color Bg = Color.FromArgb(28, 28, 34);
        public static readonly Color Border = Color.FromArgb(70, 255, 255, 255);
        public static readonly Color Text = Color.FromArgb(235, 235, 240);
        public static readonly Color Hover = Color.FromArgb(124, 92, 252);
        public static readonly Color HoverPressed = Color.FromArgb(96, 68, 205);
        public static readonly Color Separator = Color.FromArgb(60, 255, 255, 255);
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => DarkColor.Bg;
        public override Color ImageMarginGradientBegin => DarkColor.Bg;
        public override Color ImageMarginGradientMiddle => DarkColor.Bg;
        public override Color ImageMarginGradientEnd => DarkColor.Bg;
        public override Color MenuBorder => DarkColor.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => DarkColor.Hover;
        public override Color MenuItemSelectedGradientBegin => DarkColor.Hover;
        public override Color MenuItemSelectedGradientEnd => DarkColor.Hover;
        public override Color MenuItemPressedGradientBegin => DarkColor.HoverPressed;
        public override Color MenuItemPressedGradientEnd => DarkColor.HoverPressed;
        public override Color SeparatorDark => DarkColor.Separator;
        public override Color SeparatorLight => DarkColor.Separator;
        public override Color ToolStripBorder => DarkColor.Border;
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rc = new Rectangle(Point.Empty, e.Item.Size);
            if (!e.Item.Selected && !e.Item.Pressed) return;
            using var brush = new SolidBrush(e.Item.Pressed ? DarkColor.HoverPressed : DarkColor.Hover);
            e.Graphics.FillRectangle(brush, rc);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(DarkColor.Separator, 1);
            e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }
    }
}
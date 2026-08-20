using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace GlassMusicPlayer.Services;

/// <summary>
/// Windows taskbar integration via ITaskbarList3: progress bar on the taskbar
/// icon, thumbnail toolbar buttons (play/pause, prev, next) and an overlay icon
/// reflecting the play state.
/// </summary>
public sealed class TaskbarService : IDisposable
{
    public const uint ButtonPlayPause = 1;
    public const uint ButtonPrev = 2;
    public const uint ButtonNext = 3;

    private const uint WM_COMMAND = 0x0111;

    private const uint THB_ICON = 0x0002;
    private const uint THB_TOOLTIP = 0x0004;
    private const uint THB_FLAGS = 0x0008;
    private const uint THBF_ENABLED = 0x0000;
    private const uint THBF_DISABLED = 0x0001;

    private const int TBPF_NOPROGRESS = 0;
    private const int TBPF_NORMAL = 0x2;
    private const int TBPF_PAUSED = 0x8;

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, int tbpFlags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, int tbpFlags);
        void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [In] THUMBBUTTON[] pButtons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [In] THUMBBUTTON[] pButtons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
        void SetThumbnailClip(IntPtr hwnd, ref RECT prcClip);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public uint idCommand;
        public uint fsState;
        public uint fsInteractive;
        public string szTip;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private readonly ITaskbarList3 _taskbar;
    private readonly ImageList _imageList;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private IntPtr _overlayPlay;
    private IntPtr _overlayPause;
    private bool _hasThumbnails;

    public event Action? PlayPauseClicked;
    public event Action? PrevClicked;
    public event Action? NextClicked;

    public TaskbarService()
    {
        var clsid = new Guid("56FDF344-FD6D-11D0-958A-006097C9A090"); // CLSID_TaskbarList
        var type = Type.GetTypeFromCLSID(clsid) ?? throw new InvalidOperationException("Taskbar COM object unavailable");
        _taskbar = (ITaskbarList3)Activator.CreateInstance(type)!;
        _taskbar.HrInit();

        _imageList = new ImageList { ImageSize = new System.Drawing.Size(16, 16), TransparentColor = Color.Magenta, ColorDepth = ColorDepth.Depth32Bit };
        _imageList.Images.Add(MakeThumbBitmap("play"));
        _imageList.Images.Add(MakeThumbBitmap("pause"));
        _imageList.Images.Add(MakeThumbBitmap("next"));
        _imageList.Images.Add(MakeThumbBitmap("prev"));

        _overlayPlay = MakeOverlayIcon(false);
        _overlayPause = MakeOverlayIcon(true);
    }

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);
        _taskbar.ThumbBarSetImageList(_hwnd, _imageList.Handle);
    }

    public void SetProgress(double current, double total, bool playing)
    {
        if (_hwnd == IntPtr.Zero || total <= 0) return;
        ulong completed = (ulong)Math.Max(0, Math.Min(total, current));
        ulong ullTotal = (ulong)total;
        _taskbar.SetProgressValue(_hwnd, completed, ullTotal);
        _taskbar.SetProgressState(_hwnd, playing ? TBPF_NORMAL : TBPF_PAUSED);
    }

    public void ClearProgress()
    {
        if (_hwnd == IntPtr.Zero) return;
        _taskbar.SetProgressState(_hwnd, TBPF_NOPROGRESS);
    }

    public void SetThumbnailButtons(bool isPlaying, bool hasTrack)
    {
        if (_hwnd == IntPtr.Zero) return;

        var buttons = new THUMBBUTTON[3];

        buttons[0] = new THUMBBUTTON
        {
            dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
            iId = ButtonPlayPause,
            iBitmap = isPlaying ? 1u : 0u,
            idCommand = ButtonPlayPause,
            fsState = hasTrack ? THBF_ENABLED : THBF_DISABLED,
            fsInteractive = hasTrack ? THBF_ENABLED : THBF_DISABLED,
            szTip = isPlaying ? "Пауза" : "Воспроизвести"
        };
        buttons[1] = new THUMBBUTTON
        {
            dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
            iId = ButtonPrev,
            iBitmap = 3u,
            idCommand = ButtonPrev,
            fsState = THBF_ENABLED,
            fsInteractive = THBF_ENABLED,
            szTip = "Предыдущий трек"
        };
        buttons[2] = new THUMBBUTTON
        {
            dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
            iId = ButtonNext,
            iBitmap = 2u,
            idCommand = ButtonNext,
            fsState = THBF_ENABLED,
            fsInteractive = THBF_ENABLED,
            szTip = "Следующий трек"
        };

        if (!_hasThumbnails)
        {
            _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
            _hasThumbnails = true;
        }
        else
        {
            _taskbar.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
        }

        SetOverlayIcon(isPlaying ? _overlayPause : _overlayPlay);
    }

    public void SetOverlayIcon(IntPtr hIcon)
    {
        if (_hwnd == IntPtr.Zero) return;
        _taskbar.SetOverlayIcon(_hwnd, hIcon, hIcon == _overlayPause ? "Playing" : "Paused");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_COMMAND)
        {
            uint id = (uint)(wParam.ToInt64() >> 16) & 0xFFFF;
            switch (id)
            {
                case ButtonPlayPause:
                    PlayPauseClicked?.Invoke();
                    handled = true;
                    break;
                case ButtonPrev:
                    PrevClicked?.Invoke();
                    handled = true;
                    break;
                case ButtonNext:
                    NextClicked?.Invoke();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private static Bitmap MakeThumbBitmap(string glyph)
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.White);
            switch (glyph)
            {
                case "play":
                    g.FillPolygon(brush, new[] { new PointF(5f, 2f), new PointF(14f, 8f), new PointF(5f, 14f) });
                    break;
                case "pause":
                    g.FillRectangle(brush, 4, 2, 3, 12);
                    g.FillRectangle(brush, 9, 2, 3, 12);
                    break;
                case "next":
                    g.FillPolygon(brush, new[] { new PointF(3f, 2f), new PointF(10f, 8f), new PointF(3f, 14f) });
                    g.FillRectangle(brush, 10, 2, 3, 12);
                    break;
                case "prev":
                    g.FillRectangle(brush, 3, 2, 3, 12);
                    g.FillPolygon(brush, new[] { new PointF(13f, 2f), new PointF(6f, 8f), new PointF(13f, 14f) });
                    break;
            }
        }
        return bmp;
    }

    private static IntPtr MakeOverlayIcon(bool playing)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(playing ? Color.FromArgb(230, 60, 60) : Color.FromArgb(50, 200, 90));
            g.FillEllipse(bg, 0, 0, 16, 16);
            using var brush = new SolidBrush(Color.White);
            if (playing)
            {
                g.FillRectangle(brush, 5, 4, 2, 8);
                g.FillRectangle(brush, 9, 4, 2, 8);
            }
            else
            {
                g.FillPolygon(brush, new[] { new PointF(5f, 3f), new PointF(13f, 8f), new PointF(5f, 13f) });
            }
        }
        var hIcon = bmp.GetHicon();
        return hIcon;
    }

    public void Dispose()
    {
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
        if (_overlayPlay != IntPtr.Zero) { DestroyIcon(_overlayPlay); _overlayPlay = IntPtr.Zero; }
        if (_overlayPause != IntPtr.Zero) { DestroyIcon(_overlayPause); _overlayPause = IntPtr.Zero; }
        _imageList.Dispose();
    }
}
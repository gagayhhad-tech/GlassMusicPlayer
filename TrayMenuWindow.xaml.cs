using System;
using System.Windows;
using System.Windows.Input;

namespace GlassMusicPlayer;

/// <summary>
/// Glass-styled context menu shown when the user right-clicks the tray icon.
/// Matches the app's liquid-glass look (translucent dark panel, soft border,
/// rounded corners, Segoe UI).
/// </summary>
public partial class TrayMenuWindow : Window
{
    public event Action? OpenWindow;
    public event Action? PlayPause;
    public event Action? Next;
    public event Action? Prev;
    public event Action? Exit;

    public TrayMenuWindow()
    {
        InitializeComponent();
    }

    public void SetTrackTitle(string? title)
    {
        TrackTitle.Text = string.IsNullOrWhiteSpace(title) ? "вЂ”" : title;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenWindow?.Invoke();
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        Hide();
        PlayPause?.Invoke();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        Hide();
        Next?.Invoke();
    }

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        Hide();
        Prev?.Invoke();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Hide();
        Exit?.Invoke();
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        Hide();
    }

private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }
}

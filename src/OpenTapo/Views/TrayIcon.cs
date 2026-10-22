using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace OpenTapo.Views;

/// <summary>Notification-area icon: left click toggles the panel, right click opens a Fluent-styled menu.</summary>
[ExcludeFromCodeCoverage(Justification = "WPF shell: needs a desktop session and a notification area.")]
public sealed partial class TrayIcon : IDisposable
{
    // A click on the icon first deactivates (hides) the panel; within this window it must not reopen it.
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

    private readonly PanelWindow _panel;
    private readonly ContextMenu _menu;
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(PanelWindow panel)
    {
        _panel = panel;
        _menu = BuildMenu();
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "open-tapo",
            Visible = true,
        };
        _icon.MouseUp += OnMouseUp;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private ContextMenu BuildMenu()
    {
        MenuItem open = new() { Header = "Open", Icon = Glyph("") };
        open.Click += (_, _) => _panel.ShowNearTray();

        MenuItem quit = new() { Header = "Quit", Icon = Glyph("") };
        quit.Click += (_, _) => Application.Current.Shutdown();

        return new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            Items = { open, new Separator(), quit },
        };
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        switch (e.Button)
        {
            case Forms.MouseButtons.Left:
                TogglePanel();
                break;
            case Forms.MouseButtons.Right:
                ShowMenu();
                break;
        }
    }

    private void TogglePanel()
    {
        if (_panel.IsVisible || DateTime.UtcNow - _panel.LastHiddenAt < ReopenGuard)
        {
            _panel.Hide();
            return;
        }

        _panel.ShowNearTray();
    }

    private void ShowMenu()
    {
        _menu.IsOpen = true;
        // A popup only closes on an outside click if it owns the foreground.
        if (PresentationSource.FromVisual(_menu) is HwndSource source)
        {
            SetForegroundWindow(source.Handle);
        }
    }

    private static TextBlock Glyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("IconFont"),
        FontSize = 14,
    };

    private static Drawing.Icon LoadIcon()
    {
        using Stream stream = Application.GetResourceStream(new Uri("pack://application:,,,/App.ico")).Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);
}

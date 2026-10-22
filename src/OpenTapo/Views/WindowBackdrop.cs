using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OpenTapo.Views;

/// <summary>Gives a window the Windows 11 flyout look: dark acrylic backdrop and rounded corners.</summary>
[ExcludeFromCodeCoverage(Justification = "WPF shell: needs a desktop session and a notification area.")]
internal static partial class WindowBackdrop
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmSystemBackdropType = 38;
    private const int CornerRound = 2;
    private const int BackdropTransientAcrylic = 3;

    public static void Apply(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        HwndSource.FromHwnd(handle).CompositionTarget.BackgroundColor = Colors.Transparent;
        SetAttribute(handle, DwmUseImmersiveDarkMode, 1);
        SetAttribute(handle, DwmWindowCornerPreference, CornerRound);
        SetAttribute(handle, DwmSystemBackdropType, BackdropTransientAcrylic);
    }

    private static void SetAttribute(IntPtr handle, int attribute, int value) =>
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

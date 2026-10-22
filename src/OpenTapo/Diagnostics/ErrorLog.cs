using System.IO;
using OpenTapo.Services;

namespace OpenTapo.Diagnostics;

/// <summary>A tray app has no console: unexpected errors go to %LOCALAPPDATA%\OpenTapo\error.log instead of vanishing.</summary>
public static class ErrorLog
{
    public static string FilePath => AppFolder.File("error.log");

    public static void Write(Exception exception)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
    }
}

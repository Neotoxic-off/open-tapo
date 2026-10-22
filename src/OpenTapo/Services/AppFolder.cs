using System.IO;

namespace OpenTapo.Services;

/// <summary>%LOCALAPPDATA%\OpenTapo, where the app keeps its files.</summary>
internal static class AppFolder
{
    /// <summary>Tests point this at a temporary folder so they never touch the user's data.</summary>
    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTapo");

    public static string File(string name) => Path.Combine(Root, name);
}

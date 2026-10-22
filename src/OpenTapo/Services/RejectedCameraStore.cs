using System.IO;
using System.Text.Json;

namespace OpenTapo.Services;

/// <summary>
/// Cameras that rejected the current account, kept across restarts: each login attempt counts against a
/// camera's 10-attempt lockout, so a known rejection is never retried until the account changes.
/// Only addresses are stored, nothing derived from the password.
/// </summary>
public sealed class RejectedCameraStore
{
    private static string FilePath => AppFolder.File("rejected-cameras.json");

    private readonly object _sync = new();

    public bool Contains(string ip)
    {
        lock (_sync)
        {
            return Load().Contains(ip);
        }
    }

    public void Add(string ip)
    {
        lock (_sync)
        {
            HashSet<string> rejected = Load();
            if (rejected.Add(ip))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(rejected));
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            // File.Delete throws when the folder does not exist yet (first sign-in with no device found).
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
    }

    private static HashSet<string> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (JsonException)
        {
            // A corrupted file is treated as empty; the worst case is one more login attempt.
            return [];
        }
    }
}

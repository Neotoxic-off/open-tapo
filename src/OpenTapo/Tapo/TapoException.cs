using System.Security.Cryptography;
using System.Text.Json;

namespace OpenTapo.Tapo;

public class TapoException(string message) : Exception(message)
{
    /// <summary>
    /// What parsing a hostile or broken reply throws: bad JSON, a field of the wrong type, an undecryptable body.
    /// Any LAN host can answer as a device, so these must end as a device error, never as a crash.
    /// </summary>
    internal static bool IsMalformedReply(Exception ex) =>
        ex is JsonException or InvalidOperationException or FormatException or OverflowException or CryptographicException or ArgumentException;
}

/// <summary>The device rejected the account credentials. Never retried, so a wrong password cannot lock the device.</summary>
public sealed class TapoAuthException(string message) : TapoException(message);

/// <summary>The camera locked sign-in after too many failed attempts.</summary>
public sealed class TapoLockedException(TimeSpan retryAfter)
    : TapoException($"Camera locked after too many sign-in attempts. Try again in {Math.Ceiling(retryAfter.TotalMinutes)} min.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

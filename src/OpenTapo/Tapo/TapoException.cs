namespace OpenTapo.Tapo;

public class TapoException(string message) : Exception(message);

/// <summary>The device rejected the account credentials. Never retried, so a wrong password cannot lock the device.</summary>
public sealed class TapoAuthException(string message) : TapoException(message);

/// <summary>The camera locked sign-in after too many failed attempts.</summary>
public sealed class TapoLockedException(TimeSpan retryAfter)
    : TapoException($"Camera locked after too many sign-in attempts. Try again in {Math.Ceiling(retryAfter.TotalMinutes)} min.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

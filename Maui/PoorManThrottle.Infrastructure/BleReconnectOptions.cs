namespace PoorManThrottle.Infrastructure;

public sealed class BleReconnectOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Total reconnect attempts after an unexpected disconnect.
    /// </summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// Base delay for attempt #1.
    /// </summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum delay cap for exponential backoff.
    /// </summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);
}
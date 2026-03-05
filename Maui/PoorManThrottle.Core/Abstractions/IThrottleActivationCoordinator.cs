using PoorManThrottle.Core.Models;

namespace PoorManThrottle.Core.Abstractions;

public interface IThrottleActivationCoordinator
{
    event EventHandler<string>? ActivationChanged; // deviceId

    ThrottleActivationState GetState(string deviceId);
    string? GetLastError(string deviceId);

    /// <summary>
    /// Ensure we are tracking this session and will activate it on connect/reconnect.
    /// </summary>
    void Track(IBleDeviceSession session);

    /// <summary>
    /// If connected, kicks activation immediately; otherwise it will activate when it connects.
    /// Throws if activation fails.
    /// </summary>
    Task EnsureActivatedAsync(string deviceId, CancellationToken cancellationToken = default);
}
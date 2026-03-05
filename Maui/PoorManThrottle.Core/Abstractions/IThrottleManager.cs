using PoorManThrottle.Core.Models;

namespace PoorManThrottle.Core.Abstractions;

/// <summary>
/// Owns session lifetime (create/store/lookup) but does not implement reconnect logic.
/// Reconnect (if any) is owned by IBleDeviceSession implementations.
/// </summary>
public interface IThrottleManager
{
    IReadOnlyCollection<IBleDeviceSession> Sessions { get; }

    IBleDeviceSession? GetSession(string deviceId);

    Task<IBleDeviceSession> ConnectAsync(
        IBleDeviceInfo device,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(
        string deviceId,
        CancellationToken cancellationToken = default);
}
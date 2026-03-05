using PoorManThrottle.Core.Abstractions;
using PoorManThrottle.Core.Models;

namespace PoorManThrottle.Infrastructure;

public sealed class InMemoryThrottleManager : IThrottleManager
{
    private readonly IBleAdapter _adapter;
    private readonly BleReconnectOptions _reconnectOptions;

    // Store WRAPPED sessions (stable identity across app lifetime)
    private readonly Dictionary<string, IBleDeviceSession> _sessions = new();

    public InMemoryThrottleManager(IBleAdapter adapter, BleReconnectOptions reconnectOptions)
    {
        _adapter = adapter;
        _reconnectOptions = reconnectOptions;
    }

    public IReadOnlyCollection<IBleDeviceSession> Sessions
        => _sessions.Values;

    public IBleDeviceSession? GetSession(string deviceId)
        => _sessions.TryGetValue(deviceId, out var s) ? s : null;

    public async Task<IBleDeviceSession> ConnectAsync(
        IBleDeviceInfo device,
        CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(device.Id, out var existing))
        {
            if (existing.ConnectionState != BleConnectionState.Connected)
                await existing.ConnectAsync(cancellationToken);

            return existing;
        }

        // Create raw BLE session (mock or real) and wrap it with app policy
        var raw = _adapter.CreateSession(device);
        var managed = new ManagedBleDeviceSession(raw, _reconnectOptions);

        _sessions[device.Id] = managed;

        await managed.ConnectAsync(cancellationToken);
        return managed;
    }

    public async Task DisconnectAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(deviceId, out var session))
            return;

        await session.DisconnectAsync(cancellationToken);

        // IMPORTANT: Do NOT remove the session.
        // Stable identity across navigation/lifetime.
    }
}
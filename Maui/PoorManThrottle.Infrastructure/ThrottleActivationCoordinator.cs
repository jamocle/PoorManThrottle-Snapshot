using System.Collections.Concurrent;
using PoorManThrottle.Core.Abstractions;
using PoorManThrottle.Core.Models;

namespace PoorManThrottle.Infrastructure;

public sealed class ThrottleActivationCoordinator : IThrottleActivationCoordinator
{
    private readonly IThrottleHandshake _handshake;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public event EventHandler<string>? ActivationChanged;

    public ThrottleActivationCoordinator(IThrottleHandshake handshake)
    {
        _handshake = handshake;
    }

    public void Track(IBleDeviceSession session)
    {
        var entry = _entries.GetOrAdd(session.DeviceId, _ => new Entry(session.DeviceId));

        if (entry.Session is null)
        {
            entry.Session = session;

            entry.ConnectionHandler = (_, state) => OnConnectionChanged(entry, state);
            session.ConnectionStateChanged += entry.ConnectionHandler;
        }
        else if (!ReferenceEquals(entry.Session, session))
        {
            // Contract violation: session objects must be stable for a given deviceId.
            throw new InvalidOperationException(
                $"Session instance replaced for deviceId '{session.DeviceId}'. " +
                "IBleDeviceSession must be stable across app lifetime.");
        }

        if (session.ConnectionState == BleConnectionState.Connected)
            _ = StartActivationIfNeededAsync(entry, CancellationToken.None);
    }

    public ThrottleActivationState GetState(string deviceId)
        => _entries.TryGetValue(deviceId, out var e) ? e.State : ThrottleActivationState.NotActivated;

    public string? GetLastError(string deviceId)
        => _entries.TryGetValue(deviceId, out var e) ? e.LastError : null;

    public Task EnsureActivatedAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(deviceId, out var entry) || entry.Session is null)
            throw new InvalidOperationException("No session tracked for activation.");

        return StartActivationIfNeededAsync(entry, cancellationToken);
    }

    private void OnConnectionChanged(Entry entry, BleConnectionState state)
    {
        // Any disconnect clears activation (firmware no longer activated)
        if (state is BleConnectionState.Disconnected or BleConnectionState.Reconnecting or BleConnectionState.Connecting)
        {
            // If we failed activation, keep the error visible after we force-disconnect.
            if (entry.State != ThrottleActivationState.Failed)
            {
                entry.State = ThrottleActivationState.NotActivated;
                entry.LastError = null;
            }

            Raise(entry.DeviceId);
            return;
        }

        if (state == BleConnectionState.Connected)
            _ = StartActivationIfNeededAsync(entry, CancellationToken.None);
    }

    private async Task StartActivationIfNeededAsync(Entry entry, CancellationToken cancellationToken)
    {
        var session = entry.Session;
        if (session is null)
            throw new InvalidOperationException("Missing session.");

        // If already activated, nothing to do
        if (entry.State == ThrottleActivationState.Activated)
            return;

        // If currently activating, await the existing task
        var existing = entry.ActivationTask;
        if (existing is not null && !existing.IsCompleted)
        {
            await existing.ConfigureAwait(false);
            return;
        }

        // Start a new activation attempt
        entry.State = ThrottleActivationState.Activating;
        entry.LastError = null;
        Raise(entry.DeviceId);

        entry.ActivationTask = DoActivateAsync(entry, session, cancellationToken);
        await entry.ActivationTask.ConfigureAwait(false);
    }

    private async Task DoActivateAsync(Entry entry, IBleDeviceSession session, CancellationToken cancellationToken)
    {
        try
        {
            await _handshake.ActivateAsync(session, cancellationToken).ConfigureAwait(false);

            // If the session changed while we were activating, don't publish stale success.
            if (!ReferenceEquals(entry.Session, session))
                return;

            entry.State = ThrottleActivationState.Activated;
            entry.LastError = null;
            Raise(entry.DeviceId);
        }
        catch (Exception ex)
        {
            // If the session changed while we were activating, don't publish stale failure.
            if (!ReferenceEquals(entry.Session, session))
                throw;

            // Record firmware ERR or timeout message; show in UI
            entry.State = ThrottleActivationState.Failed;
            entry.LastError = ex.Message;
            Raise(entry.DeviceId);

            // FORCE DISCONNECT (and stop reconnect loop because DisconnectAsync is "manual")
            try { await session.DisconnectAsync(cancellationToken).ConfigureAwait(false); }
            catch { /* ignore */ }

            throw; // bubble up so Scan "Connect failed: ..." works
        }
    }

    private void Raise(string deviceId)
        => ActivationChanged?.Invoke(this, deviceId);

    private sealed class Entry
    {
        public string DeviceId { get; }
        public IBleDeviceSession? Session;

        public EventHandler<BleConnectionState>? ConnectionHandler;

        public ThrottleActivationState State = ThrottleActivationState.NotActivated;
        public string? LastError;
        public Task? ActivationTask;

        public Entry(string deviceId)
        {
            DeviceId = deviceId;
        }
    }
}
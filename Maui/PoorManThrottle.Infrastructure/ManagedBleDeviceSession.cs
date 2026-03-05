using PoorManThrottle.Core.Abstractions;
using PoorManThrottle.Core.Models;

namespace PoorManThrottle.Infrastructure;

/// <summary>
/// Application-owned wrapper around a raw BLE session.
/// Owns reconnect policy so mock vs real BLE behave the same.
/// </summary>
public sealed class ManagedBleDeviceSession : IBleDeviceSession, IDebugDropSession
{
    private readonly IBleDeviceSession _inner;
    private readonly BleReconnectOptions _options;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _loopCts;

    private volatile bool _manualStop;         // set by DisconnectAsync (user)
    private volatile bool _reconnectInProgress;

    private BleConnectionState _publishedState;

    public string DeviceId => _inner.DeviceId;
    public string DeviceName => _inner.DeviceName;

    public BleConnectionState ConnectionState => GetEffectiveState();

    public event EventHandler<BleConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? LineReceived;

    public void SimulateDrop()
    {
        // Forward if the inner implementation supports it; otherwise no-op.
        if (_inner is IDebugDropSession d)
            d.SimulateDrop();
    }

    public ManagedBleDeviceSession(IBleDeviceSession inner, BleReconnectOptions options)
    {
        _inner = inner;
        _options = options;

        _publishedState = GetEffectiveState();

        // Forward inner events
        _inner.LineReceived += (_, line) => LineReceived?.Invoke(this, line);
        _inner.ConnectionStateChanged += InnerOnConnectionStateChanged;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // User-initiated connect clears manual stop and cancels any prior loop.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _manualStop = false;
            StopReconnectLoop_NoLock();

            await _inner.ConnectAsync(cancellationToken);
            PublishIfChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        // User/manual disconnect permanently stops reconnect until a new ConnectAsync
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _manualStop = true;
            StopReconnectLoop_NoLock();

            await _inner.DisconnectAsync(cancellationToken);
            PublishIfChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SendAsync(string command, CancellationToken cancellationToken = default)
        => _inner.SendAsync(command, cancellationToken);

    private void InnerOnConnectionStateChanged(object? sender, BleConnectionState state)
    {
        // Mirror state changes, but if we unexpectedly drop and policy allows it,
        // start application-driven reconnect attempts.
        PublishIfChanged();

        if (state == BleConnectionState.Disconnected)
        {
            if (_manualStop) return;
            if (!_options.Enabled) return;

            StartReconnectLoop();
        }
    }

    private void StartReconnectLoop()
    {
        // Fire-and-forget loop with its own CTS; guarded to prevent duplicates.
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                if (_manualStop) return;

                // If already looping, do nothing
                if (_loopCts is not null) return;

                _loopCts = new CancellationTokenSource();
                _reconnectInProgress = true;
                PublishIfChanged();
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                var token = _loopCts.Token;

                for (int attempt = 0; attempt < _options.MaxAttempts && !token.IsCancellationRequested; attempt++)
                {
                    if (_manualStop) return;

                    // attempt 0: no delay (immediate)
                    if (attempt > 0)
                    {
                        var graceMaxDelay = TimeSpan.FromSeconds(4);
                        var maxDelay = _options.MaxDelay < graceMaxDelay ? _options.MaxDelay : graceMaxDelay;

                        var delay = ComputeBackoff(attempt, _options.BaseDelay, maxDelay);                        
                        try { await Task.Delay(delay, token); }
                        catch (OperationCanceledException) { return; }
                    }

                    try
                    {
                        await _inner.ConnectAsync(token);

                        if (_inner.ConnectionState == BleConnectionState.Connected)
                        {
                            await _gate.WaitAsync();
                            try
                            {
                                StopReconnectLoop_NoLock();
                                PublishIfChanged();
                            }
                            finally
                            {
                                _gate.Release();
                            }

                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        // swallow and retry according to policy
                    }
                }
            }
            finally
            {
                await _gate.WaitAsync();
                try
                {
                    StopReconnectLoop_NoLock();
                    PublishIfChanged();
                }
                finally
                {
                    _gate.Release();
                }
            }
        });
    }

    private static TimeSpan ComputeBackoff(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        // attempt=1 => baseDelay * 1
        // attempt=2 => baseDelay * 2
        // attempt=3 => baseDelay * 4 ...
        var factor = Math.Pow(2, Math.Max(0, attempt - 1));
        var ms = baseDelay.TotalMilliseconds * factor;
        if (ms > maxDelay.TotalMilliseconds) ms = maxDelay.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(ms);
    }

    private BleConnectionState GetEffectiveState()
    {
        if (_reconnectInProgress)
            return BleConnectionState.Reconnecting;

        return _inner.ConnectionState;
    }

    private void PublishIfChanged()
    {
        var now = GetEffectiveState();
        if (now == _publishedState) return;

        _publishedState = now;
        ConnectionStateChanged?.Invoke(this, now);
    }

    private void StopReconnectLoop_NoLock()
    {
        _reconnectInProgress = false;

        if (_loopCts is null)
            return;

        _loopCts.Cancel();
        _loopCts.Dispose();
        _loopCts = null;
    }
}
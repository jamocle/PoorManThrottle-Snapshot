using System.Security.Cryptography;
using PoorManThrottle.Core.Abstractions;
using PoorManThrottle.Core.Helpers;
using PoorManThrottle.Core.Models;

namespace PoorManThrottle.Infrastructure.MockBle;

public sealed class MockBleDeviceSession : IBleDeviceSession, IDebugDropSession
{
    private readonly IBleDeviceInfo _device;
    private readonly MockBleOptions _options;
    private MockBleDeviceScenario Scenario => _options.GetScenario(_device.Id);

    private BleConnectionState _state = BleConnectionState.Disconnected;

    // Prevent overlapping connect/disconnect (transport correctness only)
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private int _throttle;
    private string _dir = "STOPPED";

    // Handshake state (per session)
    private string? _efuseHex; // 12 hex chars
    private bool _activated;

    public string DeviceId => _device.Id;
    public string DeviceName => _device.Name;
    public BleConnectionState ConnectionState => _state;

    public event EventHandler<string>? LineReceived;
    public event EventHandler<BleConnectionState>? ConnectionStateChanged;

    public MockBleDeviceSession(IBleDeviceInfo device, MockBleOptions options)
    {
        _device = device;
        _options = options;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => ConnectCoreAsync(cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Delay(_options.DisconnectDelay, cancellationToken);

            _efuseHex = null;
            _activated = false;

            SetState(BleConnectionState.Disconnected);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public Task SendAsync(string command, CancellationToken cancellationToken = default)
    {
        if (_state != BleConnectionState.Connected)
            return Task.CompletedTask;

        var upper = (command ?? string.Empty).Trim().ToUpperInvariant();

        // Allow handshake commands anytime after BLE link is up
        if (upper == "I" || upper.StartsWith("I,"))
            return ProcessFirmwareCommandAsync(command, cancellationToken);

        // Optionally require activation before accepting other commands
        if (_options.RequireActivation && !_activated)
        {
            Emit("ERR:Not activated");
            return Task.CompletedTask;
        }

        return ProcessFirmwareCommandAsync(command, cancellationToken);
    }

    public void SimulateDrop()
    {
        if (_state != BleConnectionState.Connected)
            return;

        _efuseHex = null;
        _activated = false;

        // Unexpected disconnect: wrapper/manager policy decides what happens next.
        SetState(BleConnectionState.Disconnected);
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_state == BleConnectionState.Connected)
                return;

            _efuseHex = null;
            _activated = false;

            SetState(BleConnectionState.Connecting);

            await Task.Delay(_options.ConnectDelay, cancellationToken);

            // NEW: simulate connect failure
            if (Scenario.ConnectFails)
            {
                SetState(BleConnectionState.Disconnected);
                throw new InvalidOperationException("Mock connect failed (scenario).");
            }

            SetState(BleConnectionState.Connected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(BleConnectionState.Disconnected);
            throw;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ProcessFirmwareCommandAsync(string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var upper = (command ?? string.Empty).Trim().ToUpperInvariant();

        if (upper == "I")
        {
            if (Scenario.ActivationRefused)
            {
                Emit("ERR:Activation refused");
                return;
            }

            _efuseHex = Scenario.UseFixedEfuse
                ? NormalizeEfuseOrFallback(Scenario.FixedEfuseHex)
                : GenerateRandomHex12Lower();

            Emit($"I:{_efuseHex}");
            return;
        }

        if (upper.StartsWith("I,", StringComparison.Ordinal))
        {
            if (_efuseHex is null)
            {
                Emit("ERR:Handshake missing efuse (send I first)");
                return;
            }

            var provided = command.Trim().Substring(2).Trim();
            var expected = Obfuscator.Obfuscate12(_efuseHex);

            if (!string.Equals(provided, expected, StringComparison.OrdinalIgnoreCase))
            {
                Emit("ERR:Handshake invalid token");
                return;
            }

            _activated = true;
            await Task.Delay(_options.FirmwareResponseDelay, cancellationToken);
            Emit("I:Connected");
            return;
        }

        if (upper == "B")
        {
            Emit("ACK:B");
            return;
        }

        if (upper == "S")
        {
            Emit("ACK:S");
            return;
        }

        if (upper == "?")
        {
            var state = _dir switch
            {
                "FWD" => "HW-FWD",
                "REV" => "HW-REV",
                _ => "HW-STOPPED"
            };

            Emit($"{state} M{_throttle} HW{_throttle}");
            return;
        }

        if (upper.StartsWith("F") || upper.StartsWith("R"))
        {
            if (int.TryParse(upper[1..], out var val))
            {
                _throttle = val;
                _dir = upper.StartsWith("R") ? "REV" : "FWD";
                Emit($"ACK:{upper}");
            }
        }
    }

    private static string NormalizeEfuseOrFallback(string? hex)
    {
        // Keep it simple: only accept exactly 12 hex chars, otherwise fallback deterministic value
        var s = (hex ?? string.Empty).Trim();
        if (s.Length != 12) return "001122AABBCC";
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var isHex =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F');
            if (!isHex) return "001122AABBCC";
        }
        return s;
    }

    private static string GenerateRandomHex12Lower()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void SetState(BleConnectionState state)
    {
        _state = state;
        ConnectionStateChanged?.Invoke(this, state);
    }

    private void Emit(string line)
    {
        LineReceived?.Invoke(this, line);
    }
}
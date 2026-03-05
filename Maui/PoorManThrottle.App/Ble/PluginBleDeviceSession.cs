using System.Text;
using PoorManThrottle.Core.Abstractions;
using PoorManThrottle.Core.Models;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Abstractions;

namespace PoorManThrottle.App.Ble;

public sealed class PluginBleDeviceSession : IBleDeviceSession
{
    private readonly IBleDeviceInfo _deviceInfo;

    private readonly IAdapter _adapter;
    private IDevice? _device;
    private IService? _service;
    private ICharacteristic? _rx;
    private ICharacteristic? _tx;

    private BleConnectionState _state = BleConnectionState.Disconnected;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // line framing
    private readonly StringBuilder _rxText = new();
    private readonly object _rxLock = new();

    private void ClearRxBuffer()
    {
        lock (_rxLock)
        {
            _rxText.Clear();
        }
    }

    public string DeviceId => _deviceInfo.Id;
    public string DeviceName => _deviceInfo.Name;
    public BleConnectionState ConnectionState => _state;

    public event EventHandler<BleConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? LineReceived;

    public PluginBleDeviceSession(IBleDeviceInfo deviceInfo)
    {
        _deviceInfo = deviceInfo;
        _adapter = CrossBluetoothLE.Current.Adapter;

        _adapter.DeviceDisconnected += AdapterOnDeviceDisconnected;
        _adapter.DeviceConnectionLost += AdapterOnDeviceConnectionLost;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state == BleConnectionState.Connected)
                return;

            SetState(BleConnectionState.Connecting);
            ClearRxBuffer();
            
            var id = Guid.Parse(DeviceId);

            // Get device handle (connect if needed)
            _device = await _adapter.ConnectToKnownDeviceAsync(id, cancellationToken: cancellationToken);

            // Discover service/characteristics
            _service = await _device.GetServiceAsync(BleUuids.Service);

            if (_service is null)
                throw new InvalidOperationException("Service not found on device.");

            _rx = await _service.GetCharacteristicAsync(BleUuids.Rx);
            _tx = await _service.GetCharacteristicAsync(BleUuids.Tx);

            if (_rx is null)
                throw new InvalidOperationException("RX characteristic not found.");

            if (_tx is null)
                throw new InvalidOperationException("TX characteristic not found.");

            // Subscribe to notifications
            _tx.ValueUpdated -= TxOnValueUpdated;
            _tx.ValueUpdated += TxOnValueUpdated;

            await _tx.StartUpdatesAsync();

            SetState(BleConnectionState.Connected);
        }
        catch
        {
            // ensure we don’t leave “Connecting” stuck
            SetState(BleConnectionState.Disconnected);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state == BleConnectionState.Disconnected)
                return;

            // stop notify
            try
            {
                if (_tx is not null)
                {
                    _tx.ValueUpdated -= TxOnValueUpdated;
                    await _tx.StopUpdatesAsync();
                }
            }
            catch { /* ignore */ }

            // disconnect device
            try
            {
                if (_device is not null)
                    await _adapter.DisconnectDeviceAsync(_device);
            }
            catch { /* ignore */ }

            _service = null;
            _rx = null;
            _tx = null;
            _device = null;

            ClearRxBuffer();
            SetState(BleConnectionState.Disconnected);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendAsync(string command, CancellationToken cancellationToken = default)
    {
        if (_state != BleConnectionState.Connected)
            return;

        var rx = _rx;
        if (rx is null)
            throw new InvalidOperationException("RX characteristic not ready.");

        var clean = (command ?? string.Empty).Trim();
        var bytes = Encoding.ASCII.GetBytes(clean);

        // Check characteristic properties
        var canWwr = rx.Properties.HasFlag(CharacteristicPropertyType.WriteWithoutResponse);

        rx.WriteType = canWwr
            ? CharacteristicWriteType.WithoutResponse
            : CharacteristicWriteType.WithResponse;

        await rx.WriteAsync(bytes);
    }

    private void TxOnValueUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        // Ignore late/stale notifications (common around unexpected drops + reconnect)
        if (_state != BleConnectionState.Connected)
            return;

        // Ignore callbacks not coming from the *current* TX characteristic subscription
        if (_tx is null || !ReferenceEquals(sender, _tx))
            return;

        var data = e.Characteristic?.Value;
        if (data is null || data.Length == 0)
            return;

        var text = Encoding.ASCII.GetString(data);

        lock (_rxLock)
        {
            _rxText.Append(text);

            // Split on \n, and also handle \r\n
            while (true)
            {
                var s = _rxText.ToString();
                var idx = s.IndexOf('\n');
                if (idx < 0) break;

                var line = s.Substring(0, idx).TrimEnd('\r');
                _rxText.Clear();
                _rxText.Append(s.Substring(idx + 1));

                if (!string.IsNullOrWhiteSpace(line))
                    LineReceived?.Invoke(this, line);
            }
        }
    }

    private void AdapterOnDeviceDisconnected(object? sender, DeviceEventArgs e)
    {
        if (e.Device is null) return;
        if (_device is null) return;
        if (e.Device.Id != _device.Id) return;

        // Drop could happen mid-line; flush framing buffer so we don't "merge" after reconnect.
        ClearRxBuffer();
        // Unexpected disconnects should surface as Disconnected.
        SetState(BleConnectionState.Disconnected);
    }

    private void AdapterOnDeviceConnectionLost(object? sender, DeviceErrorEventArgs e)
    {
        if (e.Device is null) return;
        if (_device is null) return;
        if (e.Device.Id != _device.Id) return;

        ClearRxBuffer();
        SetState(BleConnectionState.Disconnected);
    }

    private void SetState(BleConnectionState state)
    {
        if (_state == state) return;
        _state = state;
        ConnectionStateChanged?.Invoke(this, state);
    }
}
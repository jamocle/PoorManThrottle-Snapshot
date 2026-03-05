using PoorManThrottle.Core.Abstractions;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace PoorManThrottle.App.Ble;

public sealed class PluginBleAdapter : IBleAdapter
{
    private readonly IAdapter _adapter;

    public PluginBleAdapter()
    {
        _adapter = CrossBluetoothLE.Current.Adapter;
    }

    public async Task<IReadOnlyList<IBleDeviceInfo>> ScanAsync(
        TimeSpan scanDuration,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, IBleDeviceInfo>(StringComparer.Ordinal);
        void OnDiscovered(object? s, DeviceEventArgs e)
        {
            var d = e.Device;
            if (d is null) return;

            var id = d.Id.ToString();
            var name = string.IsNullOrWhiteSpace(d.Name) ? "Unknown" : d.Name;

            // “Scan will only show my throttles”: best filter is SERVICE UUID.
            // We start scan *for the service*, so most unrelated devices never appear.
            results[id] = new PluginBleDeviceInfo(id, name);
        }

        _adapter.DeviceDiscovered += OnDiscovered;

        try
        {
            // StartScanningForDevicesAsync(serviceUuids...) is the key filter.
            // If the platform can’t filter at the OS level, Plugin.BLE will still
            // surface devices, but typically you’ll get far fewer.
            var scanTask = _adapter.StartScanningForDevicesAsync(
                serviceUuids: new[] { BleUuids.Service });

            var delayTask = Task.Delay(scanDuration, cancellationToken);

            await Task.WhenAny(scanTask, delayTask);

            try { await _adapter.StopScanningForDevicesAsync(); } catch { /* ignore */ }

            return results.Values.ToList();
        }
        finally
        {
            _adapter.DeviceDiscovered -= OnDiscovered;
        }
    }

    public IBleDeviceSession CreateSession(IBleDeviceInfo device)
        => new PluginBleDeviceSession(device);
}
using PoorManThrottle.Core.Abstractions;

namespace PoorManThrottle.Infrastructure.MockBle;

public sealed class MockBleAdapter : IBleAdapter
{
    private readonly MockBleOptions _options;

    public MockBleAdapter(MockBleOptions options)
    {
        _options = options;
    }

    public async Task<IReadOnlyList<IBleDeviceInfo>> ScanAsync(
        TimeSpan scanDuration,
        CancellationToken cancellationToken = default)
    {
        // Prefer app-provided scan duration, but allow global knob
        var delay = scanDuration > TimeSpan.Zero ? scanDuration : _options.ScanDelay;
        await Task.Delay(delay, cancellationToken);

        return _options.Devices;
    }

    public IBleDeviceSession CreateSession(IBleDeviceInfo device)
    {
        return new MockBleDeviceSession(device, _options);
    }
}
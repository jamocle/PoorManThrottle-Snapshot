namespace PoorManThrottle.Core.Abstractions;

public interface IBleAdapter
{
    Task<IReadOnlyList<IBleDeviceInfo>> ScanAsync(
        TimeSpan scanDuration,
        CancellationToken cancellationToken = default);

    IBleDeviceSession CreateSession(IBleDeviceInfo device);
}
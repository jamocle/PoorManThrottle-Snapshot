using PoorManThrottle.Core.Abstractions;
using PoorManThrottle.Infrastructure.MockBle.Models;

namespace PoorManThrottle.Infrastructure.MockBle;

/// <summary>
/// Configurable knobs for Mock BLE transport + firmware simulation.
/// Keep "business logic" out of the mock by expressing scenarios as configuration.
/// </summary>
public sealed class MockBleOptions
{
    // Device catalog (what ScanAsync returns)
    public IReadOnlyList<IBleDeviceInfo> Devices { get; init; } =
    [
        new MockBleDeviceInfo("MOCK-001", "GScaleThrottle"),
        new MockBleDeviceInfo("MOCK-002", "Train 7"),
        new MockBleDeviceInfo("MOCK-003", "Yard Switcher"),
    ];

    // Transport simulation
    public TimeSpan ScanDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ConnectDelay { get; init; } = TimeSpan.FromMilliseconds(800);
    public TimeSpan DisconnectDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    // Firmware simulation
    public bool RequireActivation { get; init; } = true;
    public TimeSpan FirmwareResponseDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Per-device scenarios (e.g. activation refusal, deterministic efuse).
    /// Key = deviceId.
    /// </summary>
    public Dictionary<string, MockBleDeviceScenario> DeviceScenarios { get; init; } = new();

    public MockBleDeviceScenario GetScenario(string deviceId)
        => DeviceScenarios.TryGetValue(deviceId, out var s) ? s : MockBleDeviceScenario.Default;
}

public sealed record MockBleDeviceScenario(
    bool ActivationRefused = false,
    bool UseFixedEfuse = false,
    string? FixedEfuseHex = null,
    bool ConnectFails = false) // NEW
{
    public static readonly MockBleDeviceScenario Default = new();
}
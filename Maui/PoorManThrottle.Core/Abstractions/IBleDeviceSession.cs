using PoorManThrottle.Core.Models;

namespace PoorManThrottle.Core.Abstractions;

/// <summary>
/// Represents a stable BLE session for a single device.
///
/// CONTRACT / SEMANTICS (important):
/// - The session instance must remain stable across the app lifetime (do not replace the object).
/// - This interface represents the BLE transport + device IO surface (connect/disconnect/send/receive).
/// - Application policies such as auto-reconnect/backoff SHOULD be owned by an Infrastructure wrapper
///   (e.g., ManagedBleDeviceSession) so the app behaves identically with real vs mock BLE.
/// - DisconnectAsync() is considered user/manual disconnect and MUST permanently stop any
///   application-driven reconnect attempts until a new user-initiated ConnectAsync() occurs.
/// - Unexpected disconnects are surfaced via ConnectionStateChanged and may trigger reconnect
///   attempts in the wrapper layer (not required in the raw BLE implementation).
/// </summary>
public interface IBleDeviceSession
{
    string DeviceId { get; }
    string DeviceName { get; }

    BleConnectionState ConnectionState { get; }

    event EventHandler<BleConnectionState>? ConnectionStateChanged;
    event EventHandler<string>? LineReceived;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task SendAsync(string command, CancellationToken cancellationToken = default);
}
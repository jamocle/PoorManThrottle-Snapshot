namespace PoorManThrottle.Core.Abstractions;

public interface IThrottleHandshake
{
    /// <summary>
    /// Performs the firmware activation handshake:
    ///   I -> wait I:<efuse> or ERR
    ///   I,<obfuscated> -> wait I:Connected or ERR
    /// Returns the efuse hex (12 chars) on success.
    /// Throws on ERR or timeout.
    /// </summary>
    Task<string> ActivateAsync(IBleDeviceSession session, CancellationToken cancellationToken = default);
}
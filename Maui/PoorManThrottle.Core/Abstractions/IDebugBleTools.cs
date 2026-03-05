namespace PoorManThrottle.Core.Abstractions;

/// <summary>
/// Optional debug/test hooks. Production implementations may no-op.
/// Keeps UI from probing transport/session capabilities.
/// </summary>
public interface IDebugBleTools
{
    bool TrySimulateDrop(string deviceId);
}
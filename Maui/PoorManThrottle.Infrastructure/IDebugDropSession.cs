namespace PoorManThrottle.Infrastructure;

/// <summary>
/// Infrastructure-only debug hook (mock/managed transport support).
/// Not part of Core abstractions.
/// </summary>
public interface IDebugDropSession
{
    void SimulateDrop();
}
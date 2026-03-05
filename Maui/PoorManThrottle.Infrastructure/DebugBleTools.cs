using PoorManThrottle.Core.Abstractions;

namespace PoorManThrottle.Infrastructure;

public sealed class DebugBleTools : IDebugBleTools
{
    private readonly IThrottleManager _manager;

    public DebugBleTools(IThrottleManager manager)
    {
        _manager = manager;
    }

    public bool TrySimulateDrop(string deviceId)
    {
        var session = _manager.GetSession(deviceId);
        if (session is not PoorManThrottle.Infrastructure.IDebugDropSession droppable)
            return false;

        droppable.SimulateDrop();
        return true;
    }
}
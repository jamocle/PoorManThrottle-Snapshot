namespace PoorManThrottle.Core.Models;

public enum ThrottleActivationState
{
    NotActivated = 0,
    Activating = 1,
    Activated = 2,
    Failed = 3
}
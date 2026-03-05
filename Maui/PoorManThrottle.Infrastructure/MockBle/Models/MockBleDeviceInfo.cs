using PoorManThrottle.Core.Abstractions;

namespace PoorManThrottle.Infrastructure.MockBle.Models;

public sealed record MockBleDeviceInfo(string Id, string Name) : IBleDeviceInfo;
using PoorManThrottle.Core.Abstractions;

namespace PoorManThrottle.App.Ble;

public sealed record PluginBleDeviceInfo(string Id, string Name) : IBleDeviceInfo;
namespace PoorManThrottle.App.Ble;

public static class BleUuids
{
    public static readonly Guid Service = Guid.Parse("9b2b7b30-5f3d-4a51-9bd6-1e8cde2c9000");
    public static readonly Guid Rx      = Guid.Parse("9b2b7b31-5f3d-4a51-9bd6-1e8cde2c9000"); // Write / WriteNR
    public static readonly Guid Tx      = Guid.Parse("9b2b7b32-5f3d-4a51-9bd6-1e8cde2c9000"); // Notify
}
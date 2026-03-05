using PoorManThrottle.Core.Abstractions;
using PoorManThrottle.Core.Models;
using PoorManThrottle.Infrastructure;
using PoorManThrottle.Infrastructure.MockBle;
using PoorManThrottle.Infrastructure.MockBle.Models;
using System.Collections.Concurrent;

namespace PoorManThrottle.Tests;

[TestClass]
public sealed class ManagedBleDeviceSessionTests
{
    private static ManagedBleDeviceSession CreateSession(
        BleReconnectOptions? reconnectOptions = null,
        MockBleOptions? mockOptions = null)
    {
        reconnectOptions ??= new BleReconnectOptions
        {
            Enabled = true,
            MaxAttempts = 5,
            BaseDelay = TimeSpan.FromMilliseconds(20),
            MaxDelay = TimeSpan.FromMilliseconds(50),
        };

        mockOptions ??= new MockBleOptions
        {
            ConnectDelay = TimeSpan.FromMilliseconds(10),
            DisconnectDelay = TimeSpan.FromMilliseconds(1),
            RequireActivation = false, // not relevant to transport tests
        };

        IBleDeviceInfo device = new MockBleDeviceInfo("MOCK-TEST", "TestDevice");
        var raw = new MockBleDeviceSession(device, mockOptions);
        return new ManagedBleDeviceSession(raw, reconnectOptions);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, TimeSpan poll)
    {
        var start = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(poll);
        }
    }

    [TestMethod]
    public async Task UnexpectedDrop_Transitions_Reconnecting_Then_Connected()
    {
        var session = CreateSession();

        var states = new List<BleConnectionState>();
        session.ConnectionStateChanged += (_, s) => states.Add(s);

        await session.ConnectAsync();
        Assert.AreEqual(BleConnectionState.Connected, session.ConnectionState);

        // Force an unexpected drop in the inner session
        ((IDebugDropSession)session).SimulateDrop();

        // We should publish Reconnecting quickly
        await WaitUntilAsync(
            () => session.ConnectionState == BleConnectionState.Reconnecting,
            timeout: TimeSpan.FromSeconds(2),
            poll: TimeSpan.FromMilliseconds(5));

        // And then eventually publish Connected again (the bug was "stuck in Reconnecting")
        await WaitUntilAsync(
            () => session.ConnectionState == BleConnectionState.Connected,
            timeout: TimeSpan.FromSeconds(2),
            poll: TimeSpan.FromMilliseconds(10));

        // Ensure we actually observed the transitions via events (not just property polling)
        CollectionAssert.Contains(states, BleConnectionState.Reconnecting);
        CollectionAssert.Contains(states, BleConnectionState.Connected);

        // Optional: ensure Connected happens *after* Reconnecting
        var idxReconnecting = states.IndexOf(BleConnectionState.Reconnecting);
        var idxConnected = states.LastIndexOf(BleConnectionState.Connected);
        Assert.IsTrue(idxReconnecting >= 0, "Reconnecting not observed.");
        Assert.IsTrue(idxConnected > idxReconnecting, "Connected should be observed after Reconnecting.");
    }

    [TestMethod]
    public async Task ManualDisconnect_Stops_ReconnectLoop_Permanently_Until_ConnectCalledAgain()
    {
        var session = CreateSession(new BleReconnectOptions
        {
            Enabled = true,
            MaxAttempts = 10,
            BaseDelay = TimeSpan.FromMilliseconds(20),
            MaxDelay = TimeSpan.FromMilliseconds(50),
        });

        await session.ConnectAsync();
        Assert.AreEqual(BleConnectionState.Connected, session.ConnectionState);

        // Unexpected drop triggers reconnect loop
        ((IDebugDropSession)session).SimulateDrop();

        await WaitUntilAsync(
            () => session.ConnectionState == BleConnectionState.Reconnecting,
            timeout: TimeSpan.FromSeconds(2),
            poll: TimeSpan.FromMilliseconds(5));

        // User manually disconnects during reconnecting -> must permanently stop reconnect
        await session.DisconnectAsync();

        Assert.AreEqual(BleConnectionState.Disconnected, session.ConnectionState);

        // Wait a bit longer than max backoff to ensure it doesn't reconnect on its own
        await Task.Delay(300);

        Assert.AreEqual(
            BleConnectionState.Disconnected,
            session.ConnectionState,
            "Session reconnected after manual DisconnectAsync, but contract says reconnect must stop until ConnectAsync.");
    }

    [TestMethod]
    public async Task AttemptsExhausted_Ends_Disconnected_And_ReconnectingEventuallyClears()
    {
        // Arrange: connect always fails, and we allow a few retries
        var reconnectOptions = new BleReconnectOptions
        {
            Enabled = true,
            MaxAttempts = 3,
            BaseDelay = TimeSpan.FromMilliseconds(20),
            MaxDelay = TimeSpan.FromMilliseconds(40),
        };

        var mockOptions = new MockBleOptions
        {
            ConnectDelay = TimeSpan.FromMilliseconds(10),
            DisconnectDelay = TimeSpan.FromMilliseconds(1),
            RequireActivation = false,
            DeviceScenarios = new Dictionary<string, MockBleDeviceScenario>
            {
                ["MOCK-FAIL"] = new MockBleDeviceScenario(ConnectFails: true)
            }
        };

        IBleDeviceInfo device = new MockBleDeviceInfo("MOCK-FAIL", "FailingDevice");
        var raw = new MockBleDeviceSession(device, mockOptions);
        var session = new ManagedBleDeviceSession(raw, reconnectOptions);

        var states = new ConcurrentQueue<BleConnectionState>();
        session.ConnectionStateChanged += (_, s) => states.Enqueue(s);

        // Step 1: establish an initial Connected state (so a later drop triggers reconnect loop)
        mockOptions.DeviceScenarios["MOCK-FAIL"] = new MockBleDeviceScenario(ConnectFails: false);
        await session.ConnectAsync();
        Assert.AreEqual(BleConnectionState.Connected, session.ConnectionState);

        // Step 2: now force future connects to fail, and simulate an unexpected drop
        mockOptions.DeviceScenarios["MOCK-FAIL"] = new MockBleDeviceScenario(ConnectFails: true);
        ((IDebugDropSession)session).SimulateDrop();

        // We should go to Reconnecting quickly
        await WaitUntilAsync(
            () => session.ConnectionState == BleConnectionState.Reconnecting,
            timeout: TimeSpan.FromSeconds(2),
            poll: TimeSpan.FromMilliseconds(5));

        // And after attempts exhaust, we should end Disconnected (reconnecting cleared)
        await WaitUntilAsync(
            () => session.ConnectionState == BleConnectionState.Disconnected,
            timeout: TimeSpan.FromSeconds(3),
            poll: TimeSpan.FromMilliseconds(10));

        // Give it a moment to ensure it doesn't bounce back to Reconnecting
        await Task.Delay(200);

        Assert.AreEqual(
            BleConnectionState.Disconnected,
            session.ConnectionState,
            $"Expected final Disconnected. Observed events: {string.Join(", ", states)}");

        // Ensure we actually saw Reconnecting at least once
        CollectionAssert.Contains(states.ToArray(), BleConnectionState.Reconnecting);
    }
}
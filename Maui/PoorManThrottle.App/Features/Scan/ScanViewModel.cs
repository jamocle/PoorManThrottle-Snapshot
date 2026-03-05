using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorManThrottle.Core.Abstractions;
using PoorManThrottle.Core.Models;
using PoorManThrottle.App.Features.Throttle;
using System.Collections.ObjectModel;

namespace PoorManThrottle.App.Features.Scan;

public sealed partial class ScanViewModel : ObservableObject
{
    private readonly IBleAdapter _adapter;
    private readonly IThrottleManager _manager;

    public ObservableCollection<DeviceListItem> Devices { get; } = new();

    [ObservableProperty]
    private bool isScanning;

    [ObservableProperty]
    private string statusText = "Idle";

    private bool _liveAttached;

    // NEW: one-time auto-scan guard
    private bool _initialScanStarted;

    private readonly IThrottleActivationCoordinator _activation;

    public ScanViewModel(IBleAdapter adapter, IThrottleManager manager, IThrottleActivationCoordinator activation)
    {
        _adapter = adapter;
        _manager = manager;
        _activation = activation;
    }

    public async Task EnsureInitialScanAsync()
    {
        if (_initialScanStarted) return;
        _initialScanStarted = true;

        // Run scan on first appearance
        await ScanAsync(CancellationToken.None);
    }

    public void AttachLiveUpdates()
    {
        if (_liveAttached) return;
        _liveAttached = true;

        foreach (var item in Devices)
            item.Attach(_manager, _activation);
    }

    public void DetachLiveUpdates()
    {
        if (!_liveAttached) return;
        
        _liveAttached = false;

        foreach (var item in Devices)
            item.Detach();
    }

    [RelayCommand]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (IsScanning) return;

        try
        {
            IsScanning = true;
            StatusText = "Scanning...";

            DetachLiveUpdates();
            Devices.Clear();

            var results = await _adapter.ScanAsync(TimeSpan.FromSeconds(1), cancellationToken);

            foreach (var d in results)
            {
                var item = new DeviceListItem(d.Id, d.Name);
                Devices.Add(item);

                if (_liveAttached)
                    item.Attach(_manager, _activation);
                else
                    item.RefreshStateOnly(_manager, _activation);
            }

            StatusText = Devices.Count == 0 ? "No devices found" : $"Found {Devices.Count}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task ConnectAsync(DeviceListItem item, CancellationToken cancellationToken)
    {
        if (item is null) return;
        if (!item.CanConnect) return;

        try
        {
            var device = new SimpleDeviceInfo(item.Id, item.Name);

            // Start connect (session is created/stored immediately inside manager)
            var connectTask = _manager.ConnectAsync(device, cancellationToken);

            // Attach NOW so the row sees Connecting/Reconnecting state changes
            item.Attach(_manager, _activation);

            // Wait for BLE link
            var session = await connectTask;

            // Ensure coordinator tracking (safe even if Track already happened in Attach)
            _activation.Track(session);

            // Now do firmware activation handshake
            await _activation.EnsureActivatedAsync(item.Id, cancellationToken);
        }
        catch
        {
            // Row will show error via ActivationState/ActivationError
            item.RefreshStateOnly(_manager, _activation);
        }
    }

    [RelayCommand]
    private async Task OpenAsync(DeviceListItem item)
    {
        if (item is null) return;

        // NEW: only open when actually Connected
        if (!item.IsOpenable)
            return;

        await Shell.Current.GoToAsync(nameof(ThrottlePage), new Dictionary<string, object>
        {
            ["deviceId"] = item.Id
        });
    }

    public sealed partial class DeviceListItem : ObservableObject
    {
        private IBleDeviceSession? _session;
        private IThrottleActivationCoordinator? _activation;
        public string Id { get; }
        public string Name { get; }

        [ObservableProperty]
        private BleConnectionState? connectionState;

        [ObservableProperty]
        private ThrottleActivationState activationState;

        [ObservableProperty]
        private string? activationError;

        public DeviceListItem(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public void Attach(IThrottleManager manager, IThrottleActivationCoordinator activation)
        {
            Detach();

            _activation = activation;

            _session = manager.GetSession(Id);
            if (_session is null)
            {
                ConnectionState = null;
                ActivationState = ThrottleActivationState.NotActivated;
                return;
            }

            // Ensure coordinator is tracking this session
            activation.Track(_session);

            ConnectionState = _session.ConnectionState;
            ActivationState = activation.GetState(Id);

            _session.ConnectionStateChanged += SessionOnConnectionStateChanged;
            activation.ActivationChanged += ActivationOnChanged;
        }

        public void RefreshStateOnly(IThrottleManager manager, IThrottleActivationCoordinator activation)
        {
            var s = manager.GetSession(Id);
            ConnectionState = s?.ConnectionState;

            ActivationState = activation.GetState(Id);
            ActivationError = activation.GetLastError(Id);
        }


        public void Detach()
        {
            if (_session is not null)
                _session.ConnectionStateChanged -= SessionOnConnectionStateChanged;

            if (_activation is not null)
                _activation.ActivationChanged -= ActivationOnChanged;

            _session = null;
            _activation = null;
        }

        private void ActivationOnChanged(object? sender, string deviceId)
        {
            if (deviceId != Id) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_activation is not null)
                    ActivationState = _activation.GetState(Id);
                ActivationError = _activation?.GetLastError(Id);
            });
        }

        private void SessionOnConnectionStateChanged(object? sender, BleConnectionState state)
        {
            MainThread.BeginInvokeOnMainThread(() => ConnectionState = state);
        }

        public bool IsOpenable =>
            ConnectionState == BleConnectionState.Connected &&
            ActivationState == ThrottleActivationState.Activated;

        public bool IsBusy =>
            ConnectionState is BleConnectionState.Connecting or BleConnectionState.Reconnecting ||
            ActivationState == ThrottleActivationState.Activating;

        public bool CanConnect =>
            ConnectionState is null or BleConnectionState.Disconnected ||
            ActivationState == ThrottleActivationState.Failed;


        // NEW: Button enabled rules
        public bool CanTapButton =>
            IsOpenable || CanConnect;

        public string ButtonText =>
            ConnectionState == BleConnectionState.Connected
                ? "Open"
                : "Connect";

        public string StateLabel
        {
            get
            {
                // While the BLE link is changing, show that FIRST (even if last activation failed)
                if (ConnectionState == BleConnectionState.Connecting)
                    return "Connecting…";

                if (ConnectionState == BleConnectionState.Reconnecting)
                    return "Reconnecting…";

                // Once connected, show activation progress/result
                return ActivationState switch
                {
                    ThrottleActivationState.Activating => "Activating firmware…",
                    ThrottleActivationState.Activated => "Ready",
                    ThrottleActivationState.Failed => ActivationError ?? "Activation failed",
                    _ => ConnectionState?.ToString() ?? "Not connected"
                };
            }
        }


        public Color NameColor => ConnectionState switch
        {
            BleConnectionState.Connected => Colors.Green,
            BleConnectionState.Connecting => Colors.Orange,
            BleConnectionState.Reconnecting => Colors.Orange,
            _ => (Color)Application.Current!.Resources["BrandWindowTextColor"]
        };

        partial void OnConnectionStateChanged(BleConnectionState? value)
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsOpenable));
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanTapButton));
            OnPropertyChanged(nameof(ButtonText));
            OnPropertyChanged(nameof(NameColor));
            OnPropertyChanged(nameof(StateLabel));
        }

        partial void OnActivationStateChanged(ThrottleActivationState value)
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsOpenable));
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanTapButton));
            OnPropertyChanged(nameof(ButtonText));
            OnPropertyChanged(nameof(NameColor));
            OnPropertyChanged(nameof(StateLabel));
        }

        partial void OnActivationErrorChanged(string? value)
        {
            OnPropertyChanged(nameof(StateLabel));
        }
    }

    private sealed record SimpleDeviceInfo(string Id, string Name) : IBleDeviceInfo;
}
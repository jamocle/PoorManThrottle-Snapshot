using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorManThrottle.Core.Abstractions;
using PoorManThrottle.Core.Models;

namespace PoorManThrottle.App.Features.Throttle;

public sealed partial class ThrottleViewModel : ObservableObject, IQueryAttributable
{
    private readonly IThrottleManager _manager;

    private IBleDeviceSession? _session;
    private string _deviceId = "";
    private string _deviceName = "";

    private bool _isAttached;
    private bool _pendingInitialQuery;

    [ObservableProperty] private int throttleValue = 0;
    [ObservableProperty] private string direction = "FWD"; // FWD or REV
    [ObservableProperty] private string lastReply = "";

    public string DeviceId => _deviceId;

    public string HeaderText => string.IsNullOrWhiteSpace(_deviceName) ? "Throttle" : _deviceName;

    public string ConnectionText => _session is null
        ? "No session"
        : $"State: {_session.ConnectionState}";

    public Color ForwardButtonColor => Direction == "FWD" ? Colors.Green : Colors.Gray;
    public Color ReverseButtonColor => Direction == "REV" ? Colors.Green : Colors.Gray;

    // --- NEW: Badge + banner projection ---
    public BleConnectionState CurrentConnectionState =>
        _session?.ConnectionState ?? BleConnectionState.Disconnected;

    public string ConnectionBadgeText => CurrentConnectionState switch
    {
        BleConnectionState.Connected => "Connected",
        BleConnectionState.Connecting => "Connecting",
        BleConnectionState.Reconnecting => "Reconnecting",
        _ => "Disconnected"
    };

    public Color ConnectionBadgeColor => CurrentConnectionState switch
    {
        BleConnectionState.Connected => Colors.Green,
        BleConnectionState.Connecting => Colors.DodgerBlue,
        BleConnectionState.Reconnecting => Colors.Orange,
        _ => Colors.Gray
    };

    public bool ShowReconnectingBanner =>
        CurrentConnectionState == BleConnectionState.Reconnecting;

    public string ReconnectingBannerText =>
        "Reconnecting… commands will send when connected.";

    private readonly IThrottleActivationCoordinator _activation;

    public ThrottleViewModel(IThrottleManager manager, IThrottleActivationCoordinator activation)
    {
        _manager = manager;
        _activation = activation;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("deviceId", out var idObj) && idObj is string id)
            _deviceId = id;

        // We no longer attach here. The page lifecycle (OnAppearing) will call Attach().
        // But we can update header immediately if the session already exists.
        var s = _manager.GetSession(_deviceId);
        _deviceName = s?.DeviceName ?? "";

        OnPropertyChanged(nameof(HeaderText));
    }

    public void Attach()
    {
        // Idempotent: if already attached, do nothing.
        if (_isAttached)
            return;

        if (string.IsNullOrWhiteSpace(_deviceId))
        {
            LastReply = "Missing deviceId. Return to Scan.";
            return;
        }

        _session = _manager.GetSession(_deviceId);

        if (_session is null)
        {
            LastReply = "Session not found. Return to Scan and connect again.";
            OnPropertyChanged(nameof(ConnectionText));
            RaiseConnectionUiChanged();
            return;
        }

        _deviceName = _session.DeviceName;

        _session.LineReceived += OnLineReceived;
        _session.ConnectionStateChanged += OnConnectionChanged;

        _isAttached = true;

        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(ForwardButtonColor));
        OnPropertyChanged(nameof(ReverseButtonColor));
        RaiseConnectionUiChanged();

        // If we are already connected, query immediately.
        // If not, mark pending so we query as soon as we get Connected.
        if (_session.ConnectionState == BleConnectionState.Connected)
            _ = ActivateThenQueryInitialStateAsync();
        else
            _pendingInitialQuery = true;
    }

    private async Task ActivateThenQueryInitialStateAsync()
    {
        if (_session is null) return;
        if (_session.ConnectionState != BleConnectionState.Connected) return;

        try
        {
            // Make sure firmware-side activation has completed before asking "?"
            await _activation.EnsureActivatedAsync(_deviceId);

            // Now it's safe to query current state
            await _session.SendAsync("?");
        }
        catch (Exception ex)
        {
            // Don't trigger any manual disconnect here. Coordinator already handles fail/disconnect.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                LastReply = $"Activation Failed: {ex.Message}";
            });
        }
    }

    private void OnLineReceived(object? sender, string line)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LastReply = line;

            if (TryParseHwStateLine(line, out var state, out var mappedM, out var _))
            {
                // Direction from STATE
                Direction = state switch
                {
                    HwState.HwFwd => "FWD",
                    HwState.HwRev => "REV",
                    _ => Direction // keep current if stopped
                };
                OnPropertyChanged(nameof(ForwardButtonColor));
                OnPropertyChanged(nameof(ReverseButtonColor));

                // Slider uses M (mapped-equivalent)
                ThrottleValue = mappedM;
            }
        });
    }

    private enum HwState { HwStopped, HwFwd, HwRev }

    private static bool TryParseHwStateLine(string line, out HwState state, out int mappedM, out int hw)
    {
        state = HwState.HwStopped;
        mappedM = 0;
        hw = 0;

        if (string.IsNullOrWhiteSpace(line)) return false;

        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return false;

        state = parts[0] switch
        {
            "HW-STOPPED" => HwState.HwStopped,
            "HW-FWD"     => HwState.HwFwd,
            "HW-REV"     => HwState.HwRev,
            _            => state
        };

        if (parts[0] is not ("HW-STOPPED" or "HW-FWD" or "HW-REV"))
            return false;

        if (!TryParsePrefixedInt(parts[1], 'M', 0, 100, out mappedM))
            return false;

        if (!TryParseHwToken(parts[2], out hw))
            return false;

        return true;
    }

    private static bool TryParsePrefixedInt(string token, char prefix, int min, int max, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (token.Length < 2) return false;
        if (token[0] != prefix) return false;
        if (!int.TryParse(token[1..], out value)) return false;
        if (value < min || value > max) return false;
        return true;
    }

    private static bool TryParseHwToken(string token, out int value)
    {
        value = 0;
        if (!token.StartsWith("HW", StringComparison.Ordinal)) return false;
        if (!int.TryParse(token[2..], out value)) return false;
        return value is >= 0 and <= 100;
    }

    private void OnConnectionChanged(object? sender, BleConnectionState state)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            OnPropertyChanged(nameof(ConnectionText));
            RaiseConnectionUiChanged();

            // If we lost connection or are trying to recover, arm a fresh query
            // so when we get Connected again we ask the device for its current state.
            if (state is BleConnectionState.Reconnecting or BleConnectionState.Disconnected)
            {
                _pendingInitialQuery = true;
            }

            // When we get Connected, perform the query (once).
            if (state == BleConnectionState.Connected && _pendingInitialQuery)
            {
                _pendingInitialQuery = false;
                _ = ActivateThenQueryInitialStateAsync();
            }
        });
    }

    private void RaiseConnectionUiChanged()
    {
        OnPropertyChanged(nameof(CurrentConnectionState));
        OnPropertyChanged(nameof(ConnectionBadgeText));
        OnPropertyChanged(nameof(ConnectionBadgeColor));
        OnPropertyChanged(nameof(ShowReconnectingBanner));
        OnPropertyChanged(nameof(ReconnectingBannerText));
    }

    public void OnThrottleChanged(int value)
    {
        if (value < 0) value = 0;
        if (value > 100) value = 100;
        ThrottleValue = value;
    }

    public Task SendThrottleCommandAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null) return Task.CompletedTask;
        if (_session.ConnectionState != BleConnectionState.Connected) return Task.CompletedTask;

        var cmd = Direction == "REV"
            ? $"R{ThrottleValue}"
            : $"F{ThrottleValue}";

        return _session.SendAsync(cmd, cancellationToken);
    }

    [RelayCommand]
    private async Task SetForwardAsync(CancellationToken cancellationToken)
    {
        Direction = "FWD";
        OnPropertyChanged(nameof(ForwardButtonColor));
        OnPropertyChanged(nameof(ReverseButtonColor));

        await SendThrottleCommandAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task SetReverseAsync(CancellationToken cancellationToken)
    {
        Direction = "REV";
        OnPropertyChanged(nameof(ForwardButtonColor));
        OnPropertyChanged(nameof(ReverseButtonColor));

        await SendThrottleCommandAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_session is null) return;
        ThrottleValue=0;
        await _session.SendAsync("S", cancellationToken);
    }

    [RelayCommand]
    private async Task BrakeAsync(CancellationToken cancellationToken)
    {
        if (_session is null) return;
        ThrottleValue=0;
        await _session.SendAsync("B", cancellationToken);
    }

    [RelayCommand]
    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_deviceId)) return;

        await _manager.DisconnectAsync(_deviceId, cancellationToken);

        // navigate back to Scan (root)
        await Shell.Current.GoToAsync("..");
    }

    public void Detach()
    {
        if (_session is not null)
        {
            _session.LineReceived -= OnLineReceived;
            _session.ConnectionStateChanged -= OnConnectionChanged;
        }

        _session = null;
        _isAttached = false;
        _pendingInitialQuery = false;
    }
}
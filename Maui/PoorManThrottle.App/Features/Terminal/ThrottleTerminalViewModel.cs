using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorManThrottle.Core.Abstractions;
using PoorManThrottle.Core.Models;
using System.Collections.ObjectModel;

namespace PoorManThrottle.App.Features.Terminal;

public sealed partial class ThrottleTerminalViewModel : ObservableObject
{
    private readonly IThrottleManager _manager;

    private IBleDeviceSession? _session;
    private string _deviceId = "";

    public ObservableCollection<TerminalLine> OutputLines { get; } = new();
    public ObservableCollection<FunctionKeyItem> FunctionKeys { get; } = new();

    [ObservableProperty]
    private string deviceName = "";

    [ObservableProperty]
    private string headerSubtitle = "";

    [ObservableProperty]
    private string commandText = string.Empty;

    public BleConnectionState CurrentConnectionState =>
        _session?.ConnectionState ?? BleConnectionState.Disconnected;

    public bool IsInputEnabled =>
        CurrentConnectionState == BleConnectionState.Connected;

    public ThrottleTerminalViewModel(IThrottleManager manager)
    {
        _manager = manager;
    }

    public void Initialize(string deviceId)
    {
        Detach();

        _deviceId = deviceId;
        _session = _manager.GetSession(deviceId);

        OutputLines.Clear();
        CommandText = string.Empty;
        LoadFunctionKeys();

        if (_session is null)
        {
            DeviceName = "Unknown";
            HeaderSubtitle = "Connected with <missing session>";
            AddInfo("Session not found. Return to Scan and connect again.");
            NotifyStateChanged();
            return;
        }

        DeviceName = _session.DeviceName;
        HeaderSubtitle = $"Connected with {DeviceName}";

        AddInfo($"Attached to {DeviceName} ({_session.DeviceId})");
        AddInfo($"State: {_session.ConnectionState}");

        _session.LineReceived += OnLineReceived;
        _session.ConnectionStateChanged += OnConnectionStateChanged;

        NotifyStateChanged();
    }

    public void Detach()
    {
        if (_session is not null)
        {
            _session.LineReceived -= OnLineReceived;
            _session.ConnectionStateChanged -= OnConnectionStateChanged;
        }

        _session = null;
        _deviceId = "";

        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CurrentConnectionState));
        OnPropertyChanged(nameof(IsInputEnabled));
    }

    [RelayCommand]
    private Task SendAsciiAsync(CancellationToken cancellationToken)
        => SendInternalAsync(CommandText, clearEntry: true, cancellationToken);

    [RelayCommand]
    private Task SendPresetAsync(string preset, CancellationToken cancellationToken)
        => SendInternalAsync(preset, clearEntry: false, cancellationToken);

    [RelayCommand]
    private async Task TapFunctionKeyAsync(FunctionKeyItem item)
    {
        if (item is null) return;

        // If no function assigned -> open configure
        if (!item.HasCommand)
        {
            await OpenConfigureAsync(item);
            return;
        }

        // Otherwise send command
        await SendInternalAsync(item.Command, clearEntry: false, CancellationToken.None);
    }

    [RelayCommand]
    private async Task LongPressFunctionKeyAsync(FunctionKeyItem item)
    {
        if (item is null) return;

        // If no function assigned -> behave like a normal tap (current tap logic opens configure)
        if (!item.HasCommand)
        {
            await TapFunctionKeyAsync(item);
            return;
        }

        try
        {
            HapticFeedback.Default.Perform(HapticFeedbackType.LongPress);
        }
        catch
        {
            // ignore haptic failures (platform support varies)
        }

        await OpenConfigureAsync(item);
    }

    private async Task OpenConfigureAsync(FunctionKeyItem item)
    {
        // Capture current values
        var keyNum = item.KeyNumber;

        var page = new FunctionKeyConfigPage(
            keyNumber: keyNum,
            currentName: item.DisplayName,
            currentCommand: item.Command,
            onSave: (newName, newCommand) =>
            {
                item.DisplayName = newName;
                item.Command = newCommand;
                SaveFunctionKeys();
            });

        // Show modal
        await Shell.Current.Navigation.PushModalAsync(page, animated: true);
    }
    private async Task SendInternalAsync(string? command, bool clearEntry, CancellationToken cancellationToken)
    {
        var clean = (command ?? string.Empty).Trim()
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);

        if (clean.Length == 0)
            return;

        if (clearEntry)
            CommandText = string.Empty;

        AddSent(clean);

        if (_session is null)
        {
            AddInfo("No session attached.");
            return;
        }

        if (_session.ConnectionState != BleConnectionState.Connected)
        {
            AddInfo($"Not connected (state={_session.ConnectionState}).");
            return;
        }

        try
        {
            await _session.SendAsync(clean, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            AddInfo("Send cancelled.");
        }
        catch (Exception ex)
        {
            AddInfo($"Send failed: {ex.Message}");
        }
    }

    private void OnLineReceived(object? sender, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            AddReceived(line);
        });
    }

    private void OnConnectionStateChanged(object? sender, BleConnectionState state)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            AddInfo($"State: {state}");
            NotifyStateChanged();
        });
    }

    private void AddSent(string text)
        => OutputLines.Add(new TerminalLine("[<<]", text, TerminalLineKind.Sent));

    private void AddReceived(string text)
        => OutputLines.Add(new TerminalLine("[>>]", text, TerminalLineKind.Received));

    private void AddInfo(string text)
        => OutputLines.Add(new TerminalLine("[++]", text, TerminalLineKind.Info));

    private void LoadFunctionKeys()
    {
        FunctionKeys.Clear();

        var configs = TerminalFunctionKeysStore.Load();
        for (int i = 0; i < TerminalFunctionKeysStore.KeyCount; i++)
        {
            var keyNum = i + 1;
            var cfg = configs[i];

            var name = string.IsNullOrWhiteSpace(cfg.Name) ? $"#{keyNum}" : cfg.Name!;
            System.Diagnostics.Debug.WriteLine($"LoadFunctionKeys default name computed: {name}");
            FunctionKeys.Add(new FunctionKeyItem(keyNum, name, cfg.Command));
        }
    }

    private void SaveFunctionKeys()
    {
        var configs = FunctionKeys
            .Select(k => new TerminalFunctionKeysStore.FunctionKeyConfig(k.DisplayName, k.Command))
            .ToList();

        TerminalFunctionKeysStore.Save(configs);
    }

    public sealed record TerminalLine(string Prefix, string Text, TerminalLineKind Kind)
    {
        public Color PrefixColor => Kind switch
        {
            TerminalLineKind.Sent => (Color)Application.Current!.Resources["TerminalSentPrefixColor"],
            TerminalLineKind.Received => (Color)Application.Current!.Resources["TerminalReceivedPrefixColor"],
            TerminalLineKind.Info => (Color)Application.Current!.Resources["TerminalInfoPrefixColor"],
            _ => Colors.White
        };
    }

    public enum TerminalLineKind
    {
        Sent,
        Received,
        Info
    }
    public sealed partial class FunctionKeyItem : ObservableObject
    {
        public int KeyNumber { get; }

        [ObservableProperty]
        private string displayName;

        [ObservableProperty]
        private string? command;

        public bool HasCommand => !string.IsNullOrWhiteSpace(Command);

        public FunctionKeyItem(int keyNumber, string displayName, string? command)
        {
            KeyNumber = keyNumber;
            this.displayName = displayName;
            this.command = command;
        }

        partial void OnCommandChanged(string? value)
        {
            OnPropertyChanged(nameof(HasCommand));
        }
    }
}
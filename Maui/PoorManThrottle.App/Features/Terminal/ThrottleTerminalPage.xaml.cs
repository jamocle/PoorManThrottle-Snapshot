using System.Collections.Specialized;
using System.ComponentModel;

namespace PoorManThrottle.App.Features.Terminal;

public partial class ThrottleTerminalPage : ContentPage
{
    private readonly ThrottleTerminalViewModel _vm;

    public ThrottleTerminalPage(ThrottleTerminalViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    private INotifyPropertyChanged? _npc;
    
    public void Initialize(string deviceId)
    {
        _vm.Initialize(deviceId);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        _vm.OutputLines.CollectionChanged += OnOutputLinesChanged;

        // NEW: Keep page-level KeysEnabled in sync with VM.
        if (_vm is INotifyPropertyChanged npc)
            npc.PropertyChanged += VmOnPropertyChanged;

        // Initialize immediately (important)
        KeysEnabled = _vm.IsInputEnabled;

        ScrollOutputToEnd();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _vm.OutputLines.CollectionChanged -= OnOutputLinesChanged;

        if (_vm is INotifyPropertyChanged npc)
            npc.PropertyChanged -= VmOnPropertyChanged;
    }

    private bool _keysEnabled = true;

    // Binding source for DataTemplate. We will raise PropertyChanged on the Page itself.
    public bool KeysEnabled
    {
        get => _keysEnabled;
        private set
        {
            if (_keysEnabled == value) return;
            _keysEnabled = value;
            OnPropertyChanged(nameof(KeysEnabled));
        }
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThrottleTerminalViewModel.IsInputEnabled))
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                KeysEnabled = _vm.IsInputEnabled;
            });
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await CloseAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(); // fire and forget
        return true;      // we handled it
    }

    private void OnOutputLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            ScrollOutputToEnd();
    }

    private void ScrollOutputToEnd()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (_vm.OutputLines.Count <= 0) return;

            // Defer to ensure cells are realized before ScrollTo
            await Task.Yield();
            await Task.Delay(50);

            var lastIndex = _vm.OutputLines.Count - 1;
            OutputCollection.ScrollTo(lastIndex, position: ScrollToPosition.End, animate: false);
        });
    }

    private async Task CloseAsync()
    {
        _vm.Detach();
        if (Navigation.ModalStack.Count > 0)
        {
            await Navigation.PopModalAsync(animated: true);
        }
        else
        {
            await Shell.Current.GoToAsync("..");
        }
    }
}
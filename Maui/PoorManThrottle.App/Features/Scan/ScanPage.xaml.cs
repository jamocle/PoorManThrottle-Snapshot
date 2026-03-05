namespace PoorManThrottle.App.Features.Scan;

public partial class ScanPage : ContentPage
{
    public ScanPage(ScanViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        // Mode indicator dot (no text): Blue = BLE, Green = Mock
        #if MOCK
                ModeDot.BackgroundColor = Colors.Green;
        #else
                ModeDot.BackgroundColor = Colors.Blue;
        #endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (BindingContext is not ScanViewModel vm)
            return;

        vm.AttachLiveUpdates();

        // Auto-scan only the first time this page is shown (per VM lifetime)
        await vm.EnsureInitialScanAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        if (BindingContext is ScanViewModel vm)
            vm.DetachLiveUpdates();
    }
}
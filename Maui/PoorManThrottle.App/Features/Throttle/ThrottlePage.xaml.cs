using Microsoft.Extensions.DependencyInjection;
using PoorManThrottle.App.Features.Terminal;
using PoorManThrottle.Core.Abstractions;

namespace PoorManThrottle.App.Features.Throttle;

public partial class ThrottlePage : ContentPage
{
    private readonly IDebugBleTools _debugBle;

    public ThrottlePage(ThrottleViewModel vm, IDebugBleTools debugBle)
    {
        InitializeComponent();
        BindingContext = vm;
        _debugBle = debugBle;

#if DEBUG
        // Only show in Debug builds
        if (ForceDropButton is not null)
            ForceDropButton.IsVisible = true;
#endif
    }

    private void OnSliderDragCompleted(object? sender, EventArgs e)
    {
        if (BindingContext is ThrottleViewModel vm)
            _ = vm.SendThrottleCommandAsync();
    }

    private async void OnOpenTerminalClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not ThrottleViewModel tvm)
            return;

        var services = Handler?.MauiContext?.Services;
        if (services is null)
            return;

        var terminalPage = services.GetRequiredService<ThrottleTerminalPage>();
        terminalPage.Initialize(tvm.DeviceId);

        await Navigation.PushModalAsync(terminalPage, animated: true);
    }

#if DEBUG
    private void OnForceDropClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not ThrottleViewModel tvm)
            return;

        _debugBle.TrySimulateDrop(tvm.DeviceId);
    }
#else
    private void OnForceDropClicked(object? sender, EventArgs e) { }
#endif

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (BindingContext is ThrottleViewModel vm)
            vm.Detach();
    }
    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (BindingContext is ThrottleViewModel vm)
            vm.Attach();
    }

}
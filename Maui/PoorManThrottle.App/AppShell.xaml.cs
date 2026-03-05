using PoorManThrottle.App.Features.Throttle;

namespace PoorManThrottle.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Register navigation route for throttle page
        Routing.RegisterRoute(nameof(ThrottlePage), typeof(ThrottlePage));
    }
}
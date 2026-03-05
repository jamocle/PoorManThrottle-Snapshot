using System.Reflection;

namespace PoorManThrottle.App;

public partial class App : Application
{
    public static string AppVersion =>
        $"v{AppInfo.VersionString}.{AppInfo.BuildString}";

	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}
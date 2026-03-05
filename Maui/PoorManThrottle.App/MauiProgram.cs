using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using PoorManThrottle.Core.Abstractions;
using PoorManThrottle.Infrastructure.MockBle;
using PoorManThrottle.App.Features.Scan;
using PoorManThrottle.App.Features.Throttle;
using PoorManThrottle.App.Features.Terminal;
using PoorManThrottle.Infrastructure;
#if !MOCK
using PoorManThrottle.App.Ble;
#endif

namespace PoorManThrottle.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		builder.Services.AddSingleton(new BleReconnectOptions
		{
			Enabled = true,
			MaxAttempts = 5,
			BaseDelay = TimeSpan.FromSeconds(1),
			MaxDelay = TimeSpan.FromSeconds(10),
		});

		#if MOCK
		builder.Services.AddSingleton(new PoorManThrottle.Infrastructure.MockBle.MockBleOptions
		{
			// Device catalog (what the Scan page will see)
			Devices =
			[
				new PoorManThrottle.Infrastructure.MockBle.Models.MockBleDeviceInfo("MOCK-001", "GScaleThrottle"),
				new PoorManThrottle.Infrastructure.MockBle.Models.MockBleDeviceInfo("MOCK-002", "Train 7"),
				new PoorManThrottle.Infrastructure.MockBle.Models.MockBleDeviceInfo("MOCK-003", "Yard Switcher"),
				new PoorManThrottle.Infrastructure.MockBle.Models.MockBleDeviceInfo("MOCK-004", "Yard Switcher2"),
			],

			// Optional: tweak simulation timings
			ScanDelay = TimeSpan.FromSeconds(1),
			ConnectDelay = TimeSpan.FromMilliseconds(800),
			DisconnectDelay = TimeSpan.FromMilliseconds(100),

			RequireActivation = true,
			FirmwareResponseDelay = TimeSpan.FromMilliseconds(50),

			// Scenario knobs
			DeviceScenarios = new Dictionary<string, PoorManThrottle.Infrastructure.MockBle.MockBleDeviceScenario>
			{
				["MOCK-002"] = new PoorManThrottle.Infrastructure.MockBle.MockBleDeviceScenario(
					ActivationRefused: true
				),
				["MOCK-003"] = new MockBleDeviceScenario(ConnectFails: true)
			}
		});
		builder.Services.AddSingleton<IBleAdapter, MockBleAdapter>();
		#else
		builder.Services.AddSingleton<IBleAdapter, PluginBleAdapter>();
		#endif

		builder.Services.AddSingleton<IThrottleManager, InMemoryThrottleManager>();

		builder.Services.AddSingleton<IDebugBleTools, DebugBleTools>();

		builder.Services.AddTransient<ScanViewModel>();
		builder.Services.AddTransient<ScanPage>();
		builder.Services.AddTransient<ThrottleViewModel>();
		builder.Services.AddTransient<ThrottlePage>();
		builder.Services.AddTransient<ThrottleTerminalViewModel>();
		builder.Services.AddTransient<ThrottleTerminalPage>();

		builder.Services.AddSingleton<IThrottleHandshake, ThrottleHandshake>();
		builder.Services.AddSingleton<IThrottleActivationCoordinator, ThrottleActivationCoordinator>();

		#if DEBUG
				builder.Logging.AddDebug();
		#endif
			return builder.Build();
		}
}

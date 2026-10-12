using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Mossmint.Application.Backups;
using Mossmint.Application.Profiles;
using Mossmint.Desktop.Controls;
using Mossmint.Desktop.Services;
using Mossmint.Desktop.ViewModels;
using Mossmint.Desktop.Views;
using Mossmint.Persistence.Backups;
using Mossmint.Persistence.Profiles;
using Mossmint.Persistence.Security;

namespace Mossmint.Desktop;

public partial class App : Avalonia.Application
{
    // Lets tests and tools swap services (a temporary data root, fake dialogs,
    // cheaper key derivation) before the app builds its container.
    public static Action<IServiceCollection>? ConfigureOverrides { get; set; }

    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        FieldErrors.Install();
        Services = ConfigureServices().BuildServiceProvider();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ContrastTheme.Follow(this);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CreateMainWindow();

            // The daily update check, if the user turned it on: once now, then hourly; it asks GitHub at most once a day.
            var settings = ((MainWindowViewModel)desktop.MainWindow.DataContext!).Shell.Settings;
            _ = settings.CheckDailyAsync();
            new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => _ = settings.CheckDailyAsync()).Start();

            // Closing the app locks the profile: the database closes and the key is zeroed.
            desktop.Exit += (_, _) => Services.GetRequiredService<ProfileContext>().Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public MainWindow CreateMainWindow()
    {
        var window = new MainWindow { DataContext = Services.GetRequiredService<MainWindowViewModel>() };
        if (Services.GetService<AvaloniaDesktopDialogs>() is { } dialogs)
        {
            dialogs.Owner = window;
        }

        return window;
    }

    private static ServiceCollection ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => DataRoot.Locations());
        services.AddSingleton(Argon2Settings.Default);
        services.AddSingleton<IProfileStore>(sp => new ProfileStore(
            sp.GetRequiredService<ProfileLocations>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<Argon2Settings>()));
        services.AddSingleton<IBackupService>(sp => new BackupService(
            sp.GetRequiredService<ProfileLocations>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<AvaloniaDesktopDialogs>();
        services.AddSingleton<IDesktopDialogs>(sp => sp.GetRequiredService<AvaloniaDesktopDialogs>());
        services.AddSingleton<ProfileContext>();
        services.AddSingleton(sp => new AppServices(
            sp.GetRequiredService<IProfileStore>(),
            sp.GetRequiredService<IBackupService>(),
            sp.GetRequiredService<IDesktopDialogs>(),
            sp.GetRequiredService<ProfileContext>(),
            sp.GetRequiredService<TimeProvider>())
        {
            PreferencesFolder = sp.GetRequiredService<ProfileLocations>().Root,
        });
        services.AddTransient<MainWindowViewModel>();
        ConfigureOverrides?.Invoke(services);
        return services;
    }
}

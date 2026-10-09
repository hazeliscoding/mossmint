using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Mossmint.Desktop.ViewModels;
using Mossmint.Desktop.Views;

namespace Mossmint.Desktop;

public partial class App : Avalonia.Application
{
    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Services = ConfigureServices().BuildServiceProvider();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CreateMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public MainWindow CreateMainWindow() =>
        new() { DataContext = Services.GetRequiredService<MainWindowViewModel>() };

    private static ServiceCollection ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddTransient<ShellViewModel>();
        services.AddTransient<MainWindowViewModel>();
        return services;
    }
}

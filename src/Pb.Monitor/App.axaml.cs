using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Pb.Monitor;

/// <summary>The Avalonia application. Owns the bridge host for the window's lifetime.</summary>
public sealed partial class App : Application
{
    /// <summary>
    /// The bridge the window observes. Set by <c>Program</c> before the application starts, so the
    /// window can bind to it as soon as it is created.
    /// </summary>
    public BridgeHost? Host { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MonitorViewModel viewModel = Host?.ViewModel ?? Program.NewViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) => Host?.Stop();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

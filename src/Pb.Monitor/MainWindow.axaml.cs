using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Pb.Monitor;

/// <summary>Hosts the monitor surface. All of the layout lives in the view it contains.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}

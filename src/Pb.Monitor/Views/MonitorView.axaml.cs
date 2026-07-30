using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Pb.Monitor.Views;

/// <summary>The monitor surface: header, route roster, endpoint roster, totals and legal strip.</summary>
public sealed partial class MonitorView : UserControl
{
    public MonitorView() => AvaloniaXamlLoader.Load(this);
}

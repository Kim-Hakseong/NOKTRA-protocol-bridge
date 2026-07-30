using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Pb.Core.Endpoints;
using Pb.Core.Routing;

namespace Pb.Monitor;

/// <summary>
/// The palette the rosters use. Kept in one place because the design system allows exactly one
/// accent: teal means "this is live", amber means "waiting", rust means "broken", grey means
/// "parked". Nothing else gets a colour.
/// </summary>
internal static class NoktraPalette
{
    public static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x1E, 0x7C, 0x8C));
    public static readonly IBrush AccentBright = new SolidColorBrush(Color.FromRgb(0x31, 0xA9, 0xBC));
    public static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(0xB9, 0x86, 0x2F));
    public static readonly IBrush Alert = new SolidColorBrush(Color.FromRgb(0xA8, 0x41, 0x2F));
    public static readonly IBrush Reserved = new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xA4));
    public static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x7E, 0x85, 0x88));
    public static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x13));
}

/// <summary>Colours the header chip and its dot from the overall health.</summary>
public sealed class HealthBrushConverter : IValueConverter
{
    private readonly bool _isDot;

    private HealthBrushConverter(bool isDot) => _isDot = isDot;

    /// <summary>Background of the status chip.</summary>
    public static HealthBrushConverter Chip { get; } = new HealthBrushConverter(isDot: false);

    /// <summary>The small dot inside the status chip.</summary>
    public static HealthBrushConverter Dot { get; } = new HealthBrushConverter(isDot: true);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool healthy = value is true;

        if (_isDot)
        {
            return healthy
                ? new SolidColorBrush(Color.FromRgb(0xCF, 0xF3, 0xF8))
                : new SolidColorBrush(Color.FromRgb(0xF6, 0xD9, 0xD3));
        }

        return healthy ? NoktraPalette.Accent : NoktraPalette.Alert;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The status chip is display-only.");
}

/// <summary>Colours a route row from its health.</summary>
public sealed class RouteHealthBrushConverter : IValueConverter
{
    public static RouteHealthBrushConverter Instance { get; } = new RouteHealthBrushConverter();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RouteHealth health
            ? health switch
            {
                RouteHealth.Ok => NoktraPalette.Accent,
                RouteHealth.Starting => NoktraPalette.Warn,
                RouteHealth.SourceFault or RouteHealth.SinkFault => NoktraPalette.Alert,
                _ => NoktraPalette.Reserved,
            }
            : NoktraPalette.Reserved;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The roster is display-only.");
}

/// <summary>Colours an endpoint row from its connection state.</summary>
public sealed class EndpointStateBrushConverter : IValueConverter
{
    private readonly bool _isDetail;

    private EndpointStateBrushConverter(bool isDetail) => _isDetail = isDetail;

    /// <summary>The state dot.</summary>
    public static EndpointStateBrushConverter Instance { get; } = new EndpointStateBrushConverter(isDetail: false);

    /// <summary>The detail line, which turns rust only when there is a real failure to report.</summary>
    public static EndpointStateBrushConverter Detail { get; } = new EndpointStateBrushConverter(isDetail: true);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        EndpointState state = value is EndpointState parsed ? parsed : EndpointState.Disconnected;

        if (_isDetail)
        {
            return state == EndpointState.Faulted ? NoktraPalette.Alert : NoktraPalette.Muted;
        }

        return state switch
        {
            EndpointState.Connected => NoktraPalette.Accent,
            EndpointState.Connecting => NoktraPalette.Warn,
            EndpointState.Faulted => NoktraPalette.Alert,
            _ => NoktraPalette.Reserved,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The roster is display-only.");
}

/// <summary>Turns a 0..1 fraction into an arc sweep, leaving a small gap so a full ring still reads as a gauge.</summary>
public sealed class SweepConverter : IValueConverter
{
    /// <summary>The largest sweep drawn, in degrees.</summary>
    public const double MaxSweep = 320.0;

    public static SweepConverter Instance { get; } = new SweepConverter();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double fraction = value is double parsed && double.IsFinite(parsed) ? Math.Clamp(parsed, 0, 1) : 0;
        return fraction * MaxSweep;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The gauge is display-only.");
}

/// <summary>Hides a row's detail line when there is nothing to say.</summary>
public sealed class NotNullConverter : IValueConverter
{
    public static NotNullConverter Instance { get; } = new NotNullConverter();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? !string.IsNullOrWhiteSpace(text) : value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Visibility is display-only.");
}

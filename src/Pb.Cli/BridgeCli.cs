using System.Globalization;
using Pb.Core.Configuration;
using Pb.Core.Diagnostics;
using Pb.Core.Endpoints;
using Pb.Core.Routing;
using Pb.Core.Time;

namespace Pb.Cli;

/// <summary>Process exit codes, so a supervisor script can tell the failure modes apart.</summary>
public static class ExitCodes
{
    /// <summary>The command did what was asked.</summary>
    public const int Success = 0;

    /// <summary>The configuration is invalid; nothing was started.</summary>
    public const int InvalidConfiguration = 1;

    /// <summary>The command line itself was wrong.</summary>
    public const int UsageError = 2;

    /// <summary>The bridge started but failed while running.</summary>
    public const int RuntimeFailure = 3;
}

/// <summary>
/// The command-line application, with its output and clock injected so every command can be
/// driven from a test rather than from a terminal.
/// </summary>
public sealed class BridgeCli
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly ITimeSource _time;
    private readonly RouterOptions _routerOptions;

    public BridgeCli(TextWriter output, TextWriter error, ITimeSource? time = null, RouterOptions? routerOptions = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _time = time ?? SystemTimeSource.Instance;
        _routerOptions = routerOptions ?? RouterOptions.Default;
    }

    /// <summary>Product name shown by <c>--version</c> and in the banner.</summary>
    public const string ProductName = "Noktra Protocol Bridge";

    /// <summary>Product version shown by <c>--version</c>.</summary>
    public const string Version = "0.1.0";

    /// <summary>The usage text, also shown when the command line is wrong.</summary>
    public static string UsageText => $"""
        {ProductName} {Version}
        Channel mapping protocol bridge — offline-first.

        Usage:
          bridge run <config.yaml> [options]
          bridge check <config.yaml>
          bridge --help
          bridge --version

        Commands:
          run     Validate the configuration, then move data until interrupted with Ctrl+C.
          check   Validate the configuration and the endpoint wiring, print the topology, and exit.

        Options for run:
          --quiet                    Only report warnings and errors.
          --stats-interval <seconds> Print a status report on this interval. 0 disables it. Default 10.

        Exit codes:
          {ExitCodes.Success}  success
          {ExitCodes.InvalidConfiguration}  invalid configuration
          {ExitCodes.UsageError}  wrong command line
          {ExitCodes.RuntimeFailure}  failed while running
        """;

    /// <summary>Runs one command.</summary>
    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            _error.WriteLine("No command given.");
            _error.WriteLine();
            _error.WriteLine(UsageText);
            return ExitCodes.UsageError;
        }

        switch (args[0])
        {
            case "--help" or "-h" or "help":
                _output.WriteLine(UsageText);
                return ExitCodes.Success;

            case "--version" or "-v" or "version":
                _output.WriteLine($"{ProductName} {Version}");
                return ExitCodes.Success;

            case "check":
                return CheckCommand(args);

            case "run":
                return await RunCommand(args, cancellationToken).ConfigureAwait(false);

            default:
                _error.WriteLine($"Unknown command '{args[0]}'.");
                _error.WriteLine();
                _error.WriteLine(UsageText);
                return ExitCodes.UsageError;
        }
    }

    private int CheckCommand(string[] args)
    {
        if (!TryReadPath(args, out string? path, out int usageFailure))
        {
            return usageFailure;
        }

        if (!TryLoad(path!, out BridgeConfig? config))
        {
            return ExitCodes.InvalidConfiguration;
        }

        Dictionary<string, IEndpoint>? endpoints = null;

        try
        {
            // Creating the endpoints validates every driver setting and every channel's address and
            // direction. Nothing connects, so 'check' is safe to run against a live plant.
            endpoints = EndpointFactory.CreateAll(config!);
            _output.Write(BridgeStatusFormatter.Describe(config!));
            _output.WriteLine();
            _output.WriteLine($"OK — {path} is valid.");
            return ExitCodes.Success;
        }
        catch (ConfigException ex)
        {
            ReportDiagnostics(path!, ex);
            return ExitCodes.InvalidConfiguration;
        }
        finally
        {
            if (endpoints is not null)
            {
                DisposeAll(endpoints);
            }
        }
    }

    private async Task<int> RunCommand(string[] args, CancellationToken cancellationToken)
    {
        if (!TryReadPath(args, out string? path, out int usageFailure))
        {
            return usageFailure;
        }

        bool quiet = false;
        TimeSpan statsInterval = TimeSpan.FromSeconds(10);

        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--quiet" or "-q":
                    quiet = true;
                    break;

                case "--stats-interval":
                    if (i + 1 >= args.Length
                        || !double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                        || seconds < 0
                        || !double.IsFinite(seconds))
                    {
                        _error.WriteLine("--stats-interval needs a non-negative number of seconds.");
                        return ExitCodes.UsageError;
                    }

                    statsInterval = TimeSpan.FromSeconds(seconds);
                    i++;
                    break;

                default:
                    _error.WriteLine($"Unknown option '{args[i]}'.");
                    _error.WriteLine();
                    _error.WriteLine(UsageText);
                    return ExitCodes.UsageError;
            }
        }

        if (!TryLoad(path!, out BridgeConfig? config))
        {
            return ExitCodes.InvalidConfiguration;
        }

        Dictionary<string, IEndpoint> endpoints;

        try
        {
            endpoints = EndpointFactory.CreateAll(config!);
        }
        catch (ConfigException ex)
        {
            ReportDiagnostics(path!, ex);
            return ExitCodes.InvalidConfiguration;
        }

        IBridgeLog log = new DelegateBridgeLog((level, source, message, exception) =>
        {
            if (quiet && level == BridgeLogLevel.Info)
            {
                return;
            }

            TextWriter writer = level == BridgeLogLevel.Error ? _error : _output;
            writer.WriteLine($"[{level.ToString().ToLowerInvariant()}] {source}: {message}");

            if (exception is not null && level == BridgeLogLevel.Error)
            {
                writer.WriteLine($"        {exception.GetType().Name}: {exception.Message}");
            }
        });

        try
        {
            await using BridgeRouter router = new BridgeRouter(config!, endpoints, _time, _routerOptions, log);

            _output.WriteLine($"{ProductName} {Version} — running '{config!.Name}' from {path}. Ctrl+C to stop.");

            Task run = router.RunAsync(cancellationToken);
            Task stats = ReportStatsAsync(router, statsInterval, cancellationToken);

            await run.ConfigureAwait(false);
            await stats.ConfigureAwait(false);

            _output.WriteLine();
            _output.Write(BridgeStatusFormatter.Report(router.Snapshot()));
            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Success;
        }
        catch (ConfigException ex)
        {
            ReportDiagnostics(path!, ex);
            return ExitCodes.InvalidConfiguration;
        }
        catch (Exception ex)
        {
            _error.WriteLine($"The bridge stopped: {ex.Message}");
            return ExitCodes.RuntimeFailure;
        }
        finally
        {
            DisposeAll(endpoints);
        }
    }

    /// <summary>Prints a status report on the requested interval until the bridge stops.</summary>
    private async Task ReportStatsAsync(BridgeRouter router, TimeSpan interval, CancellationToken cancellationToken)
    {
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _time.Delay(interval, cancellationToken).ConfigureAwait(false);
                _output.WriteLine($"[status] {BridgeStatusFormatter.Summary(router.Snapshot())}");
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
    }

    private bool TryReadPath(string[] args, out string? path, out int usageFailure)
    {
        path = null;
        usageFailure = ExitCodes.UsageError;

        if (args.Length < 2 || args[1].Length == 0 || args[1].StartsWith('-'))
        {
            _error.WriteLine($"'{args[0]}' needs the path of a configuration file.");
            _error.WriteLine();
            _error.WriteLine(UsageText);
            return false;
        }

        path = args[1];
        return true;
    }

    private bool TryLoad(string path, out BridgeConfig? config)
    {
        config = null;

        if (!File.Exists(path))
        {
            _error.WriteLine($"No configuration file at '{path}'.");
            return false;
        }

        try
        {
            config = BridgeConfigLoader.LoadFile(path);
            return true;
        }
        catch (ConfigException ex)
        {
            ReportDiagnostics(path, ex);
            return false;
        }
        catch (IOException ex)
        {
            _error.WriteLine($"Could not read '{path}': {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _error.WriteLine($"Could not read '{path}': {ex.Message}");
            return false;
        }
    }

    private void ReportDiagnostics(string path, ConfigException exception)
    {
        _error.WriteLine($"{path} is not a valid configuration ({exception.Diagnostics.Count} problem(s)):");

        foreach (ConfigDiagnostic diagnostic in exception.Diagnostics)
        {
            _error.WriteLine($"  {diagnostic}");
        }
    }

    private static void DisposeAll(Dictionary<string, IEndpoint> endpoints)
    {
        foreach (IEndpoint endpoint in endpoints.Values)
        {
            endpoint.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

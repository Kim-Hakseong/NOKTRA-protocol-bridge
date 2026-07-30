using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pb.Monitor.Views;

namespace Pb.Monitor;

/// <summary>Entry point of the monitor window.</summary>
public static class Program
{
    /// <summary>How often the window re-reads the bridge's state.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Logical size the off-screen render uses, matching the window's default size.</summary>
    public static readonly Size RenderSize = new Size(1180, 720);

    private const string UsageText = """
        Noktra Protocol Bridge — monitor 0.1.0

        Usage:
          Pb.Monitor [--config <config.yaml>]
          Pb.Monitor --screenshot <out.png> [--scale <n>]
          Pb.Monitor --smoke

        Options:
          --config <path>      Run this configuration and show its state. Without it the window opens idle.
          --screenshot <path>  Render the monitor surface to a PNG without opening a window.
          --scale <n>          Pixel scale for --screenshot. Default 2, which is what layout review uses.
          --smoke              Initialise the UI stack and exit. Used by the build.
          --help               Show this text.
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(UsageText);
            return 0;
        }

        Options? options = Options.Parse(args, out string? usageError);

        if (options is null)
        {
            Console.Error.WriteLine(usageError);
            Console.Error.WriteLine();
            Console.Error.WriteLine(UsageText);
            return 2;
        }

        if (options.ScreenshotPath is not null)
        {
            return Screenshot(options.ScreenshotPath, options.Scale);
        }

        // A launch check that loads the XAML and builds the window without needing a display.
        if (options.Smoke)
        {
            return Smoke();
        }

        MonitorViewModel viewModel = NewViewModel();
        BridgeHost host = new BridgeHost(viewModel);

        try
        {
            if (options.ConfigurationPath is not null)
            {
                host.Start(options.ConfigurationPath);
            }

            AppBuilder builder = BuildAvaloniaApp();
            builder.AfterSetup(setup =>
            {
                if (setup.Instance is App app)
                {
                    app.Host = host;
                }

                DispatcherTimer timer = new DispatcherTimer { Interval = RefreshInterval };
                timer.Tick += (_, _) => host.Refresh();
                timer.Start();
            });

            return builder.StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Builds the Avalonia application. Also used by the launch check and the renderer.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();

    /// <summary>A view model stamped with today's date for the header chip.</summary>
    internal static MonitorViewModel NewViewModel() => new MonitorViewModel
    {
        DateText = DateTimeOffset.Now.ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant(),
    };

    /// <summary>What the command line asked for.</summary>
    internal sealed record Options(string? ConfigurationPath, string? ScreenshotPath, int Scale, bool Smoke)
    {
        /// <summary>Parses the command line, returning null and a message when it is wrong.</summary>
        public static Options? Parse(string[] args, out string? usageError)
        {
            usageError = null;
            string? config = null;
            string? screenshot = null;
            int scale = 2;
            bool smoke = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config" or "-c":
                        if (!TryTakeValue(args, ref i, out config))
                        {
                            usageError = "--config needs the path of a configuration file.";
                            return null;
                        }

                        break;

                    case "--screenshot":
                        if (!TryTakeValue(args, ref i, out screenshot))
                        {
                            usageError = "--screenshot needs an output path.";
                            return null;
                        }

                        break;

                    case "--scale":
                        if (!TryTakeValue(args, ref i, out string? scaleText)
                            || !int.TryParse(scaleText, NumberStyles.None, CultureInfo.InvariantCulture, out scale)
                            || scale is < 1 or > 4)
                        {
                            usageError = "--scale needs a whole number between 1 and 4.";
                            return null;
                        }

                        break;

                    case "--smoke":
                        smoke = true;
                        break;

                    case "--help" or "-h":
                        break;

                    default:
                        usageError = $"Unknown option '{args[i]}'.";
                        return null;
                }
            }

            return new Options(config, screenshot, scale, smoke);
        }

        private static bool TryTakeValue(string[] args, ref int index, out string? value)
        {
            if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
            {
                value = null;
                return false;
            }

            value = args[++index];
            return true;
        }
    }

    /// <summary>
    /// Renders the monitor surface to a PNG without a display, so the layout can be reviewed at 2x
    /// during the build. Reviewing at 2x is what catches overlap and overflow that 1x hides.
    /// </summary>
    private static int Screenshot(string path, int scale)
    {
        try
        {
            BuildAvaloniaApp().SetupWithoutStarting();

            MonitorViewModel viewModel = NewViewModel();
            viewModel.ConfigurationPath = DemoStatus.ConfigurationPath;
            viewModel.Update(DemoStatus.Create());

            MonitorView view = new MonitorView
            {
                DataContext = viewModel,
                Width = RenderSize.Width,
                Height = RenderSize.Height,
            };

            // The view needs a styling root or its templated children have no template and nothing
            // draws. An unshown window is enough: it makes the application's styles reachable.
            MainWindow host = new MainWindow { Content = view, DataContext = viewModel };
            host.ApplyTemplate();

            // The item rosters only build their rows during a real layout pass, which needs a live
            // top level. The window is shown, laid out, captured and closed.
            host.Width = RenderSize.Width;
            host.Height = RenderSize.Height;
            host.ShowInTaskbar = false;
            host.Show();

            for (int pass = 0; pass < 16; pass++)
            {
                Dispatcher.UIThread.RunJobs();
                view.Measure(RenderSize);
                view.Arrange(new Rect(RenderSize));
            }

            PixelSize pixels = new PixelSize((int)RenderSize.Width * scale, (int)RenderSize.Height * scale);
            using RenderTargetBitmap bitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
            bitmap.Render(view);

            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            bitmap.Save(path);
            host.Close();

            Console.WriteLine($"OK — rendered {pixels.Width}x{pixels.Height} to {path}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Render failed: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    /// <summary>
    /// Initialises the UI stack and builds the main window, then exits. This is what makes a broken
    /// XAML file or a bad binding fail the build rather than the first launch.
    /// </summary>
    private static int Smoke()
    {
        try
        {
            BuildAvaloniaApp().SetupWithoutStarting();

            MonitorViewModel viewModel = NewViewModel();
            MainWindow window = new MainWindow { DataContext = viewModel };
            viewModel.Update(DemoStatus.Create());

            Console.WriteLine($"OK — UI initialised, window '{window.Title}' built, view model bound.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Launch check failed: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }
}

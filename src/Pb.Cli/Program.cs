using System.Runtime.InteropServices;
using Pb.Cli;

using CancellationTokenSource shutdown = new CancellationTokenSource();

// The bridge is meant to run unattended, so it must stop cleanly however the stop arrives:
// Ctrl+C in a terminal, or SIGTERM/SIGINT from launchd, systemd or a container runtime. The first
// signal requests shutdown so in-flight writes finish and the final report prints; a second one
// gives up and lets the runtime terminate the process.
void RequestShutdown()
{
    shutdown.Cancel();
}

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = !shutdown.IsCancellationRequested;
    RequestShutdown();
};

// Console.CancelKeyPress only fires with a controlling terminal, which a service does not have.
List<PosixSignalRegistration> signals = [];

foreach (PosixSignal signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT })
{
    try
    {
        signals.Add(PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = !shutdown.IsCancellationRequested;
            RequestShutdown();
        }));
    }
    catch (PlatformNotSupportedException)
    {
        // Not every signal exists on every platform; the ones that do are enough.
    }
}

try
{
    BridgeCli cli = new BridgeCli(Console.Out, Console.Error);
    return await cli.ExecuteAsync(args, shutdown.Token).ConfigureAwait(false);
}
finally
{
    foreach (PosixSignalRegistration registration in signals)
    {
        registration.Dispose();
    }
}

using System.Globalization;
using System.Runtime.Versioning;

namespace PipeWire.NET.SampleConsole;

// Command-driven explorer for both packages: graph inspection and control on one side,
// streaming and in-graph DSP on the other. With no arguments it connects and lists the
// graph, which is also what CI runs headless: connecting and enumerating must always work,
// while anything that makes sound or changes state needs an explicit command.
internal static class Program
{
    private const int Ok = 0;
    private const int NothingToDo = 1;
    private const int UsageError = 2;

    [SupportedOSPlatform("linux")]
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("This sample is Linux-only (PipeWire is a Linux daemon).");
            return UsageError;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        string command = args.Length > 0 ? args[0] : "list";
        string[] rest = args.Length > 1 ? args[1..] : [];

        // Ctrl+C during setup (connect, link, wait) surfaces as cancellation from library
        // awaits. The per-second loops handle it themselves; this is the backstop for the rest.
        try
        {
            return command switch
            {
                "list" => await GraphCommands.ListAsync(cts.Token).ConfigureAwait(false),
                "monitor" => await GraphCommands.MonitorAsync(cts.Token).ConfigureAwait(false),
                "volume" => await GraphCommands.VolumeAsync(rest, cts.Token).ConfigureAwait(false),
                "defaults" => await GraphCommands.DefaultsAsync(cts.Token).ConfigureAwait(false),
                "capture-audio" => await StreamCommands.CaptureAudioAsync(rest, cts.Token).ConfigureAwait(false),
                "capture-video" => await StreamCommands.CaptureVideoAsync(rest, cts.Token).ConfigureAwait(false),
                "filter" => await ServeCommands.FilterAsync(rest, cts.Token).ConfigureAwait(false),
                "serve" => await ServeCommands.ServeAsync(cts.Token).ConfigureAwait(false),
                "help" or "--help" or "-h" => Usage(),
                _ => Unknown(command),
            };
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("Usage: PipeWire.NET.SampleConsole [command] [options]");
        Console.WriteLine("  list                         connect and print the graph (default)");
        Console.WriteLine("  monitor                      print a line per graph change until Ctrl+C");
        Console.WriteLine("  volume [target]              print volume and mute (default: default sink)");
        Console.WriteLine("    --set 0..1                 set the volume; needs an explicit value");
        Console.WriteLine("    --mute | --unmute          flip the mute flag");
        Console.WriteLine("  defaults                     default sink/source and graph clock settings");
        Console.WriteLine("  capture-audio [--seconds N]  capture stats from the default source");
        Console.WriteLine("  capture-video [--seconds N]  capture stats from the default source");
        Console.WriteLine("  filter [--seconds N]         tone -> gain node -> default sink");
        Console.WriteLine("  serve                        publish a virtual source until Ctrl+C");
        Console.WriteLine("Targets are node ids or name fragments. Ctrl+C stops any command.");
        return Ok;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Try 'help'.");
        return UsageError;
    }

    // --seconds N, defaulting when absent or unparsable. Never throws on user input.
    internal static int Seconds(string[] args, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--seconds", StringComparison.Ordinal))
                continue;

            if (int.TryParse(args[i + 1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int seconds)
                && seconds > 0 && seconds <= 3600)
                return seconds;

            Console.Error.WriteLine($"Ignoring bad --seconds value '{args[i + 1]}'.");
            return fallback;
        }

        return fallback;
    }

    internal static bool HasFlag(string[] args, string flag)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, flag, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    internal static string? OptionValue(string[] args, string option)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], option, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PipeWire.NET.Tests;

/// <summary>
/// One external command-line program, resolved and run.
/// </summary>
/// <remarks>
/// Every tool wrapper was repeating the same plumbing, and each copy got the cancellation handling
/// slightly differently - one of them leaked a child process on every cancelled test. Resolution is
/// through PATH with an environment override rather than a hardcoded prefix, because /usr/bin is
/// one distribution's layout and CI images do not all share it.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class CliTool
{
    private CliTool(string name, string path)
    {
        Name = name;
        Path = path;
    }

    /// <summary>The program name, as it would be typed.</summary>
    public string Name { get; }

    /// <summary>Its resolved absolute path.</summary>
    public string Path { get; }

    /// <summary>Resolves a tool, or returns null when it is not installed.</summary>
    public static CliTool? Find(string name)
    {
        string envKey = "PWNET_TEST_" + name.Replace('-', '_').ToUpperInvariant();
        if (Environment.GetEnvironmentVariable(envKey) is { Length: > 0 } overridden)
            return File.Exists(overridden) ? new CliTool(name, overridden) : null;

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = System.IO.Path.Combine(dir, name);
            if (File.Exists(candidate)) return new CliTool(name, candidate);
        }

        return null;
    }

    /// <summary>Resolves a tool, or skips the calling test when it is not installed.</summary>
    public static CliTool Require(string name) =>
        Find(name) ?? SkipBecauseMissing(name);

    private static CliTool SkipBecauseMissing(string name)
    {
        Assert.Inconclusive($"{name} is not installed.");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Runs the tool to completion and captures both streams.</summary>
    /// <exception cref="TimeoutException">It did not exit within <paramref name="timeout"/>.</exception>
    public async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string[] args, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(Path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {Path}");

        Task<string> stdout = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = proc.StandardError.ReadToEndAsync(cancellationToken);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            await proc.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            // Killed on any cancellation, not only the timeout: a child left running outlives the
            // test and interferes with every one after it.
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }

            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{Name} did not exit");
        }

        return (proc.ExitCode, await stdout, await stderr);
    }

    /// <summary>Starts the tool and leaves it running under the caller's control.</summary>
    public Process Start(params string[] args)
    {
        var psi = new ProcessStartInfo(Path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        return Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {Path}");
    }
}

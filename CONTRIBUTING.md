# Contributing

Thanks for your interest in PipeWire.NET.

## Building

```sh
dotnet build PipeWire.NET.slnx
dotnet test --filter "TestCategory!=Integration"
```

The build treats warnings as errors and targets .NET 10 (and .NET 11 preview).

Integration tests need a running PipeWire daemon on Linux. Some use GStreamer to produce real
sources; install `gstreamer1.0-tools gstreamer1.0-plugins-base gstreamer1.0-plugins-good
gstreamer1.0-pipewire`. Tests tagged `RequiresGpu` need a DRM render node and are skipped otherwise.

## Generated bindings

Files under `src/PipeWire.NET/generated/` are produced by ClangSharp from the PipeWire headers
and are committed. Do not edit them by hand. To regenerate after a header change, run
`generate/generate.sh` on Linux with `libpipewire-0.3-dev` and `libclang-dev` installed. CI fails
if the committed output does not match a fresh generation.

Hand-written code that extends the generated `Native` class lives in
`src/PipeWire.NET/Native.Extensions.cs`, outside the generated folder.

## Pull requests

Keep changes focused. Make sure the build is clean and the non-integration tests pass. If you
change the native surface, regenerate and commit the bindings in the same PR.

## License

By contributing you agree that your contributions are licensed under the MIT License.

## House rules

- **Warnings are errors.** `TreatWarningsAsErrors` is on. Fix the diagnostic rather than suppressing
  it; a `NoWarn` or `#pragma` needs a comment saying why the rule genuinely does not apply.
- **Nullable reference types are enabled** everywhere. No `!` without a reason.
- **All I/O is async**, with a `CancellationToken` accepted and propagated. No `.Result`,
  `.GetAwaiter().GetResult()`, or `Thread.Sleep`.
- **Public API carries XML documentation.**
- **The package is trim- and AOT-clean.** `IsAotCompatible` is set, so the trim and AOT analyzers run
  on every build. Serialization goes through a source-generated `JsonSerializerContext`, never the
  reflection-based `JsonSerializer` overloads.

## Tests

- Name tests `{Method}_{Scenario}_{ExpectedResult}`.
- Prefer the purpose-built MSTest assertions (`Assert.HasCount`, `Assert.Contains`,
  `Assert.AreSequenceEqual`) over hand-rolled equality checks. The analyzers will point you at them.
- No `Thread.Sleep`. Use `TaskCompletionSource`, channels, or a fake clock.
- New behaviour needs a test. Bug fixes need a test that fails before the fix.

## Commits and pull requests

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/):

```
fix(webhooks): reject a signature computed over the decoded body
```

Keep the subject under 50 characters and in the imperative mood. Add a body only when the reason for
the change would not be obvious to the next reader. Explain *why*, not *what*.

One logical change per commit. Rebase rather than merge when updating a branch.

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating you are
expected to uphold it.

## Reporting security issues

Please do not open a public issue. See [SECURITY.md](SECURITY.md).

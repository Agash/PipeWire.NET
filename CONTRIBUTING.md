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

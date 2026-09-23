using System.Runtime.CompilerServices;

// The media package is a second assembly of the same library, so it sees the SPA pod and
// context internals rather than forcing them public before that is a considered decision.
[assembly: InternalsVisibleTo("PipeWire.NET.Media")]
[assembly: InternalsVisibleTo("PipeWire.NET.Tests")]

// Note: [DisableRuntimeMarshalling] is emitted by ClangSharpPInvokeGenerator into
// generated/DisableRuntimeMarshalling.g.cs (controlled by generate-disable-runtime-marshalling).

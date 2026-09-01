using System.Runtime.CompilerServices;

// Format mapping and buffer arithmetic are internal, but they are exactly the code most worth
// testing directly: a wrong answer there corrupts video rather than throwing.
[assembly: InternalsVisibleTo("PipeWire.NET.Tests")]

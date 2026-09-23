using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// One parameter an object has, and what may be done with it.
/// </summary>
/// <param name="Parameter">Which parameter.</param>
/// <param name="CanRead">Whether it can be enumerated.</param>
/// <param name="CanWrite">Whether it can be written.</param>
/// <remarks>
/// A node advertises <c>Props</c> as readable and writable and <c>Format</c> as write-only;
/// enumerating the second is an error rather than an empty answer, which is what makes this worth
/// checking rather than guessing.
/// </remarks>
public readonly record struct PipeWireParameterInfo(SpaParamType Parameter, bool CanRead, bool CanWrite);

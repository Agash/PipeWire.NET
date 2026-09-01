using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// Creation-time properties that apply to any object the registry makes.
/// </summary>
/// <remarks>
/// Held as plain fields rather than an <c>spa_dict</c>: the dictionary is a <c>ref struct</c> over
/// stack memory, which cannot survive the <see langword="await"/> in <c>ExecuteAsync</c>, so the
/// options are collected first and marshalled in one synchronous step at the end.
/// </remarks>
internal readonly record struct PipeWireObjectOptions(bool Linger, bool Passive);

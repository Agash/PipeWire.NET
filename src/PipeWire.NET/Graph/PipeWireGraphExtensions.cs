using System.Collections.Immutable;
using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// Convenience views over graph objects.
/// </summary>
/// <remarks>
/// Extension members rather than properties on the entities: the entities stay pure data, and
/// anything needing the graph takes a <see cref="PipeWireGraphSnapshot"/> explicitly.
/// </remarks>
[SupportedOSPlatform("linux")]
public static class PipeWireGraphExtensions
{
    extension(PipeWirePort port)
    {
        /// <summary>True for a data input (<c>port.direction=in</c>).</summary>
        public bool IsDataInput => port.PortDirection is PipeWirePortDirection.In;

        /// <summary>True for a data output (<c>port.direction=out</c>).</summary>
        public bool IsDataOutput => port.PortDirection is PipeWirePortDirection.Out;

        /// <summary>True for a control port (<c>port.direction=control</c>).</summary>
        public bool IsControl => port.PortDirection is PipeWirePortDirection.Control;

        /// <summary>True for a notification port (<c>port.direction=notify</c>).</summary>
        public bool IsNotify => port.PortDirection is PipeWirePortDirection.Notify;
    }

    extension(IPipeWireObject obj)
    {
        /// <summary>
        /// True when this client holds <see cref="PipeWirePermissions.Execute"/> on the object.
        /// </summary>
        /// <remarks>
        /// Not an authorisation check, and not usable as one. Anything that modifies the object
        /// also needs <see cref="PipeWirePermissions.Write"/>, and permissions can be revoked
        /// between reading this and making the call, so a caller that branches on it has decided
        /// something the daemon decides again anyway. Handle
        /// <see cref="PipeWireException.IsPermissionDenied"/> on the call instead; this is for
        /// showing a person what they may do, not for deciding whether to try.
        /// </remarks>
        public bool CanInvokeMethods => obj.Permissions.HasFlag(PipeWirePermissions.Execute);
    }

    extension(PipeWireGraphSnapshot graph)
    {
        /// <summary>The ports of a node, in the given direction.</summary>
        public IEnumerable<PipeWirePort> GetPortsForNode(uint nodeId, PipeWirePortDirection direction)
        {
            foreach (PipeWirePort port in graph.GetPortsForNode(nodeId))
                if (port.PortDirection == direction)
                    yield return port;
        }

        /// <summary>The node that owns a port, or <see langword="null"/> if it has left the graph.</summary>
        public PipeWireNode? GetNodeForPort(PipeWirePort port) => graph.GetNode(port.NodeId);

        /// <summary>Both endpoints of a link, if both are still present.</summary>
        public (PipeWirePort? Output, PipeWirePort? Input) GetEndpoints(PipeWireLink link) =>
            (graph.GetPort(link.LinkOutputPort), graph.GetPort(link.LinkInputPort));

        /// <summary>
        /// True when this node has a data output port, so media can be read from it.
        /// </summary>
        /// <remarks>
        /// Answered from ports rather than from <c>media.class</c>, which is why an audio sink
        /// comes back true: its monitor ports are outputs. Whether the daemon will authorise a
        /// particular link is decided when the link is created, not here.
        /// </remarks>
        public bool CanCaptureFrom(PipeWireNode node)
        {
            ArgumentNullException.ThrowIfNull(node);
            foreach (PipeWirePort port in graph.GetPortsForNode(node.NodeId))
                if (port.IsDataOutput) return true;
            return false;
        }

        /// <summary>True when this node has a data input port, so media can be sent to it.</summary>
        public bool CanSendTo(PipeWireNode node)
        {
            ArgumentNullException.ThrowIfNull(node);
            foreach (PipeWirePort port in graph.GetPortsForNode(node.NodeId))
                if (port.IsDataInput) return true;
            return false;
        }

        /// <summary>Nodes carrying video that media can actually be read from.</summary>
        /// <remarks>
        /// A method rather than a property: it filters the whole graph and allocates every time it
        /// is called, which is not what a property should cost. Hold the result rather than reading
        /// it in a loop.
        /// </remarks>
        public ImmutableArray<PipeWireNode> GetVideoSources() =>
            [.. graph.Nodes.Where(n => n.Media is PipeWireMediaKind.Video && graph.CanCaptureFrom(n))];

        /// <summary>Nodes carrying audio that media can actually be read from.</summary>
        /// <remarks>Includes sinks, which are readable through their monitor ports.</remarks>
        public ImmutableArray<PipeWireNode> GetAudioSources() =>
            [.. graph.Nodes.Where(n => n.Media is PipeWireMediaKind.Audio && graph.CanCaptureFrom(n))];
    }
}

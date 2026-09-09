using System.Runtime.InteropServices;

namespace PipeWire.NET;

/// <summary>An owned file descriptor, closed on disposal.</summary>
/// <remarks>
/// Not <see cref="Microsoft.Win32.SafeHandles.SafeFileHandle"/>: a dmabuf is not a file, and that
/// type advertises <c>RandomAccess</c> and <c>FileStream</c> operations which compile against one
/// and then misbehave. The BCL has no descriptor-neutral handle to use instead.
/// </remarks>
public sealed partial class SafeDescriptorHandle : SafeHandle
{
    /// <summary>Wraps <paramref name="descriptor"/>, which this handle then owns.</summary>
    public SafeDescriptorHandle(int descriptor) : base(new IntPtr(-1), ownsHandle: true)
        => SetHandle(new IntPtr(descriptor));

    /// <summary>An owning handle over no descriptor.</summary>
    public SafeDescriptorHandle() : base(new IntPtr(-1), ownsHandle: true)
    {
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == new IntPtr(-1);

    /// <summary>The descriptor, for a call that takes one. Does not transfer ownership.</summary>
    /// <exception cref="ObjectDisposedException">The handle has been disposed.</exception>
    public int Descriptor => IsClosed || IsInvalid
        ? throw new ObjectDisposedException(nameof(SafeDescriptorHandle))
        : (int)handle;

    /// <inheritdoc/>
    protected override bool ReleaseHandle() => close((int)handle) == 0;

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int close(int fd);
}

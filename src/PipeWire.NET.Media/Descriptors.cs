using System.Runtime.InteropServices;

namespace PipeWire.NET.Media;

/// <summary>Duplicating a borrowed dmabuf descriptor so it can outlive the handler that saw it.</summary>
internal static partial class Descriptors
{
    /// <summary>Duplicates <paramref name="fd"/>, or returns -1 when it is not a descriptor.</summary>
    /// <exception cref="IOException">The kernel refused to duplicate it.</exception>
    internal static int Duplicate(long fd)
    {
        if (fd < 0) return -1;

        // Range-checked before the narrowing cast. A descriptor is an int on Linux, so a value
        // outside that range did not come from the kernel and truncating it names a different file.
        if (fd > int.MaxValue)
            throw new IOException($"descriptor {fd} is not a file descriptor this process can hold.");

        int copy = Dup((int)fd);
        if (copy < 0)
            throw new IOException($"dup of descriptor {fd} failed with errno {Marshal.GetLastPInvokeError()}.");

        return copy;
    }

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static partial int Dup(int fd);
}

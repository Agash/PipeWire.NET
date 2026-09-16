using System.Runtime.CompilerServices;

// Format mapping and buffer arithmetic are internal, but they are exactly the code most worth
// testing directly: a wrong answer there corrupts video rather than throwing.
[assembly: InternalsVisibleTo("PipeWire.NET.Tests")]

// The same marshalling policy as the core assembly, whose generated code sets it. This assembly
// hands the core's function-pointer types [UnmanagedCallersOnly] methods of its own, and those types
// carry C's bool - blittable only under this attribute. Without it, a callback such as
// PipeWireStreamCore.DoPublishDriverClock (spa_invoke_func_t, whose async parameter is a bool) is
// refused by the runtime at the moment native code calls it: the exception is raised before the
// method body, unwinds through the native frame that called it, and so skips that frame's cleanup.
// Called through pw_loop_locked, that cleanup was the loop mutex's unlock - the thread kept the
// loop lock and the next dispose deadlocked joining the loop thread.
[assembly: DisableRuntimeMarshalling]

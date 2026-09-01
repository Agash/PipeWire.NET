using PipeWire.NET.Generated;

namespace PipeWire.NET.Spa;

/// <summary>SPA pod type / object type constants - pulled from generated <see cref="Native"/>.</summary>
internal static class SpaType
{
    internal const uint None       = Native.SPA_TYPE_None;
    internal const uint Bool       = Native.SPA_TYPE_Bool;
    internal const uint Id         = Native.SPA_TYPE_Id;
    internal const uint Int        = Native.SPA_TYPE_Int;
    internal const uint Long       = Native.SPA_TYPE_Long;
    internal const uint Float      = Native.SPA_TYPE_Float;
    internal const uint Double     = Native.SPA_TYPE_Double;
    internal const uint String     = Native.SPA_TYPE_String;
    internal const uint Bytes      = Native.SPA_TYPE_Bytes;
    internal const uint Rectangle  = Native.SPA_TYPE_Rectangle;
    internal const uint Fraction   = Native.SPA_TYPE_Fraction;
    internal const uint Bitmap     = Native.SPA_TYPE_Bitmap;
    internal const uint Array      = Native.SPA_TYPE_Array;
    internal const uint Struct     = Native.SPA_TYPE_Struct;
    internal const uint Object     = Native.SPA_TYPE_Object;
    internal const uint Sequence   = Native.SPA_TYPE_Sequence;
    internal const uint Pointer    = Native.SPA_TYPE_Pointer;
    internal const uint Fd         = Native.SPA_TYPE_Fd;
    internal const uint Choice     = Native.SPA_TYPE_Choice;
    internal const uint Pod        = Native.SPA_TYPE_Pod;

    internal const uint ObjectFormat           = Native.SPA_TYPE_OBJECT_Format;
    internal const uint ObjectParamBuffers     = Native.SPA_TYPE_OBJECT_ParamBuffers;
    internal const uint ObjectParamMeta        = Native.SPA_TYPE_OBJECT_ParamMeta;
    internal const uint ObjectParamIo          = Native.SPA_TYPE_OBJECT_ParamIO;
    internal const uint ObjectParamProfile     = Native.SPA_TYPE_OBJECT_ParamProfile;
    internal const uint ObjectParamPortConfig  = Native.SPA_TYPE_OBJECT_ParamPortConfig;
    internal const uint ObjectParamRoute       = Native.SPA_TYPE_OBJECT_ParamRoute;
    internal const uint ObjectProfiler         = Native.SPA_TYPE_OBJECT_Profiler;
    internal const uint ObjectParamLatency     = Native.SPA_TYPE_OBJECT_ParamLatency;

    // SPA data plane types (spa_data.type) - pulled from spa_data_type enum
    internal const uint DataMemPtr  = (uint)spa_data_type.SPA_DATA_MemPtr;
    internal const uint DataMemFd   = (uint)spa_data_type.SPA_DATA_MemFd;
    internal const uint DataDmaBuf  = (uint)spa_data_type.SPA_DATA_DmaBuf;
}

using System.Runtime.InteropServices;

namespace VHACD.Interop;

/// <summary>P/Invoke surface of native/vhacd/vhacd_capi.h.</summary>
internal static unsafe partial class NativeMethods
{
    private const string Lib = "vhacd_native";

    /// <summary>Must match VHACD_NET_CAPI_VERSION in vhacd_capi.h.</summary>
    internal const int ExpectedCapiVersion = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Params
    {
        public int StructSize;
        public uint MaxConvexHulls;
        public uint Resolution;
        public double MinVolumePercentError;
        public uint MaxRecursionDepth;
        public int ShrinkWrap;
        public int FillMode;
        public uint MaxVerticesPerHull;
        public int Multithreaded;
        public uint MinEdgeLength;
        public int FindBestPlane;
    }

    [LibraryImport(Lib)]
    internal static partial int vhacd_net_capi_version();

    [LibraryImport(Lib)]
    internal static partial int vhacd_net_run(float* verts, int nv, int* tris, int nt, in Params p,
        out nint result, byte* errBuf, int errBufLen);

    [LibraryImport(Lib)]
    internal static partial int vhacd_net_result_count(nint result);

    [LibraryImport(Lib)]
    internal static partial int vhacd_net_result_hull(nint result, int index,
        out float* verts, out int nv, out int* tris, out int nt);

    [LibraryImport(Lib)]
    internal static partial void vhacd_net_result_free(nint result);
}

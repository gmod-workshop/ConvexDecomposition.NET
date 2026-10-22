using System.Runtime.InteropServices;

namespace CuACD.Interop;

/// <summary>P/Invoke surface of native/cuacd_capi.h.</summary>
internal static unsafe partial class NativeMethods
{
    private const string Lib = "cuacd_native";

    /// <summary>Must match CUACD_CAPI_VERSION in cuacd_capi.h.</summary>
    internal const int ExpectedCapiVersion = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DecomposeParams
    {
        public int StructSize;
        public int MaxIters;
        public int Width;
        public int Width2;
        public float Threshold;
        public int Depth;
        public int QuickDepth;
        public int MaxNCutting;
        public int Verbose;
        public int DecomposeComponents;
        public int DecomposeComponentsPerIter;
        public int NConcaveEdges;
        public float ConcaveEps;
        public float ConcaveThreshold;
        public int ConcaveIters;
        public int MergeHulls;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PartView
    {
        public float* Verts;
        public int* Tris;
        public int Nv;
        public int Nt;
        public float* HullVerts;
        public int* HullTris;
        public int HullNv;
        public int HullNt;
        public float MeshVol;
        public float HullVol;
    }

    [LibraryImport(Lib)]
    internal static partial int cuacd_capi_version();

    [LibraryImport(Lib)]
    internal static partial int cuacd_query_devices(out int count, byte* errBuf, int errBufLen);

    [LibraryImport(Lib)]
    internal static partial void cuacd_set_fatbin(void* data, nuint len);

    [LibraryImport(Lib)]
    internal static partial int cuacd_create(int device, ulong poolBytes, out nint ctx, byte* errBuf, int errBufLen);

    [LibraryImport(Lib)]
    internal static partial void cuacd_destroy(nint ctx);

    [LibraryImport(Lib)]
    internal static partial byte* cuacd_last_error(nint ctx);

    [LibraryImport(Lib)]
    internal static partial int cuacd_device_name(nint ctx, byte* buf, int bufLen);

    [LibraryImport(Lib)]
    internal static partial nuint cuacd_pool_usage(nint ctx);

    [LibraryImport(Lib)]
    internal static partial int cuacd_decompose(nint ctx, float* verts, int nv, int* tris, int nt,
        in DecomposeParams p, out nint result);

    [LibraryImport(Lib)]
    internal static partial int cuacd_result_count(nint result);

    [LibraryImport(Lib)]
    internal static partial int cuacd_result_part(nint result, int index, out PartView part);

    [LibraryImport(Lib)]
    internal static partial void cuacd_result_free(nint result);

    [LibraryImport(Lib)]
    internal static partial int cuacd_check_mesh(nint ctx, float* verts, int nv, int* tris, int nt, out uint flags);

    [LibraryImport(Lib)]
    internal static partial int cuacd_preprocess(nint ctx, float* verts, int nv, int* tris, int nt, int resolution,
        out float* outVerts, out int outNv, out int* outTris, out int outNt);

    [LibraryImport(Lib)]
    internal static partial int cuacd_convex_hull(nint ctx, float* pts, int n,
        out float* outVerts, out int outNv, out int* outTris, out int outNt, out float volume);

    [LibraryImport(Lib)]
    internal static partial void cuacd_free(void* p);
}

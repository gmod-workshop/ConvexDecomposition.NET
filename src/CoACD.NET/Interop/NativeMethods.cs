using System.Runtime.InteropServices;

namespace CoACD.Interop;

/// <summary>P/Invoke surface of native/coacd/coacd_capi.h.</summary>
internal static unsafe partial class NativeMethods
{
    private const string Lib = "coacd_native";

    /// <summary>Must match COACD_NET_CAPI_VERSION in coacd_capi.h.</summary>
    internal const int ExpectedCapiVersion = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Params
    {
        public int StructSize;
        public double Threshold;
        public int MaxConvexHull;
        public int PreprocessMode;
        public int PrepResolution;
        public int SampleResolution;
        public int MctsNodes;
        public int MctsIteration;
        public int MctsMaxDepth;
        public int Pca;
        public int Merge;
        public int Decimate;
        public int MaxChVertex;
        public int Extrude;
        public double ExtrudeMargin;
        public int ApxMode;
        public uint Seed;
        public int RealMetric;
    }

    [LibraryImport(Lib)]
    internal static partial int coacd_net_capi_version();

    [LibraryImport(Lib)]
    internal static partial int coacd_net_set_log_level(byte* level, byte* errBuf, int errBufLen);

    [LibraryImport(Lib)]
    internal static partial int coacd_net_run(float* verts, int nv, int* tris, int nt, in Params p,
        out nint result, byte* errBuf, int errBufLen);

    [LibraryImport(Lib)]
    internal static partial int coacd_net_result_count(nint result);

    [LibraryImport(Lib)]
    internal static partial int coacd_net_result_hull(nint result, int index,
        out float* verts, out int nv, out int* tris, out int nt);

    [LibraryImport(Lib)]
    internal static partial void coacd_net_result_free(nint result);
}

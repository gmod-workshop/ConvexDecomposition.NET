// cuacd_capi.h — flat C ABI over the CuACD host layer, for P/Invoke.
//
// Every call that touches the GPU pushes the context's CUDA context onto the
// calling thread for the duration of the call, so a context may be used from
// any thread (but not from two threads at once — callers serialize).
//
// Return codes: 0 = success, non-zero = failure; cuacd_last_error() then holds
// a human-readable message until the next call on that context.

#ifndef CUACD_CAPI_H
#define CUACD_CAPI_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  define CUACD_API __declspec(dllexport)
#else
#  define CUACD_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define CUACD_CAPI_VERSION 1

typedef struct cuacd_context cuacd_context;
typedef struct cuacd_result  cuacd_result;

// Mirrors the knobs of the upstream lookahead_decompose(); see README for
// meaning and defaults. Input geometry must already be normalized (largest
// extent ≈ 2) — the managed layer does that.
typedef struct cuacd_decompose_params {
    int   struct_size;          // sizeof(cuacd_decompose_params), for ABI checks
    int   max_iters;
    int   width;
    int   width2;
    float threshold;
    int   depth;
    int   quick_depth;
    int   max_n_cutting;
    int   verbose;
    int   decompose_components;
    int   decompose_components_per_iter;
    int   n_concave_edges;
    float concave_eps;
    float concave_threshold;
    int   concave_iters;
    int   merge_hulls;
} cuacd_decompose_params;

// Borrowed view of one output part; pointers stay valid until
// cuacd_result_free().
typedef struct cuacd_part_view {
    const float* verts;       // nv * 3
    const int*   tris;        // nt * 3
    int          nv;
    int          nt;
    const float* hull_verts;  // hull_nv * 3
    const int*   hull_tris;   // hull_nt * 3
    int          hull_nv;
    int          hull_nt;
    float        mesh_vol;
    float        hull_vol;
} cuacd_part_view;

CUACD_API int cuacd_capi_version(void);

// Counts CUDA devices CuACD can run on (compute capability >= 7.0) without
// creating a context. Returns non-zero (message in err_buf) if the driver
// cannot be initialized.
CUACD_API int cuacd_query_devices(int* out_count, char* err_buf, int err_buf_len);

// Install the kernel fatbinary. `data` must stay valid for the process
// lifetime (it is re-read by every cuacd_create()). Call before cuacd_create.
CUACD_API void cuacd_set_fatbin(const void* data, size_t len);

// device: CUDA ordinal, or -1 to reuse the calling thread's current CUDA
//         context (falls back to device 0 when there is none).
// pool_bytes: device heap pool; 0 = auto (80% of free VRAM).
// On failure *out is NULL and the message is written to err_buf.
CUACD_API int  cuacd_create(int device, uint64_t pool_bytes, cuacd_context** out,
                            char* err_buf, int err_buf_len);
CUACD_API void cuacd_destroy(cuacd_context* ctx);
CUACD_API const char* cuacd_last_error(cuacd_context* ctx);

CUACD_API int    cuacd_device_name(cuacd_context* ctx, char* buf, int buf_len);
CUACD_API size_t cuacd_pool_usage(cuacd_context* ctx);

// Full lookahead decomposition. The input's convex hull is computed on the
// GPU first. *out must be freed with cuacd_result_free().
CUACD_API int cuacd_decompose(cuacd_context* ctx,
                              const float* verts, int nv,
                              const int* tris, int nt,
                              const cuacd_decompose_params* params,
                              cuacd_result** out);
CUACD_API int  cuacd_result_count(const cuacd_result* res);
CUACD_API int  cuacd_result_part(const cuacd_result* res, int index, cuacd_part_view* out);
CUACD_API void cuacd_result_free(cuacd_result* res);

// Topology audit → MAV_* bitmask (see cuda/mesh_audit.cuh).
CUACD_API int cuacd_check_mesh(cuacd_context* ctx,
                               const float* verts, int nv,
                               const int* tris, int nt,
                               uint32_t* out_flags);

// PaMO stage-1 remesh to a watertight manifold. Outputs are allocated by the
// library; release them with cuacd_free().
CUACD_API int cuacd_preprocess(cuacd_context* ctx,
                               const float* verts, int nv,
                               const int* tris, int nt,
                               int resolution,
                               float** out_verts, int* out_nv,
                               int** out_tris, int* out_nt);

// Exact convex hull of a point cloud (k-DOP prefilter + D&C), outward
// oriented. Outputs are released with cuacd_free().
CUACD_API int cuacd_convex_hull(cuacd_context* ctx,
                                const float* pts, int n,
                                float** out_verts, int* out_nv,
                                int** out_tris, int* out_nt,
                                float* out_volume);

CUACD_API void cuacd_free(void* p);

#ifdef __cplusplus
}
#endif

#endif // CUACD_CAPI_H

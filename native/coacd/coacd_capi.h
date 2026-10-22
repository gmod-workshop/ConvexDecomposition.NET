// coacd_capi.h — exception-safe flat C ABI over CoACD, for P/Invoke.
//
// Differences from upstream's own extern "C" API (public/coacd.h):
//   - C++ exceptions are caught and reported as an error code + message
//     instead of unwinding into the caller (which kills a .NET process).
//   - float input/output (CoACD normalizes internally, so precision is moot).
//   - results are owned by an opaque handle and freed without leaking.
// Every function is safe to call from any thread.

#ifndef COACD_NET_CAPI_H
#define COACD_NET_CAPI_H

#include <stdint.h>

#if defined(_WIN32)
#  define COACD_NET_API __declspec(dllexport)
#else
#  define COACD_NET_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define COACD_NET_CAPI_VERSION 1

typedef struct coacd_net_result coacd_net_result;

// Mirrors coacd::CoACD()'s parameters (see external/coacd/public/coacd.h).
typedef struct coacd_net_params {
    int          struct_size;        // sizeof(coacd_net_params), for ABI checks
    double       threshold;
    int          max_convex_hull;    // -1 = unlimited
    int          preprocess_mode;    // 0 auto, 1 on, 2 off
    int          prep_resolution;
    int          sample_resolution;
    int          mcts_nodes;
    int          mcts_iteration;
    int          mcts_max_depth;
    int          pca;
    int          merge;
    int          decimate;
    int          max_ch_vertex;
    int          extrude;
    double       extrude_margin;
    int          apx_mode;           // 0 convex hull, 1 box
    unsigned int seed;
    int          real_metric;
} coacd_net_params;

COACD_NET_API int coacd_net_capi_version(void);

// level: "off", "debug", "info", "warn", "error" or "critical".
COACD_NET_API int coacd_net_set_log_level(const char* level, char* err_buf, int err_buf_len);

// Runs the decomposition. On failure returns non-zero, *out is NULL and the
// message is written to err_buf.
COACD_NET_API int coacd_net_run(const float* verts, int nv,
                                const int* tris, int nt,
                                const coacd_net_params* params,
                                coacd_net_result** out,
                                char* err_buf, int err_buf_len);

COACD_NET_API int  coacd_net_result_count(const coacd_net_result* res);
// Borrowed pointers, valid until coacd_net_result_free().
COACD_NET_API int  coacd_net_result_hull(const coacd_net_result* res, int index,
                                         const float** verts, int* nv,
                                         const int** tris, int* nt);
COACD_NET_API void coacd_net_result_free(coacd_net_result* res);

#ifdef __cplusplus
}
#endif

#endif // COACD_NET_CAPI_H

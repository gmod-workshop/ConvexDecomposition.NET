/*
 * Flat C ABI over V-HACD 4 (header-only C++), for P/Invoke.
 *
 * Every entry point catches C++ exceptions and reports them through the
 * caller's error buffer, so nothing unwinds across the language boundary.
 * Each call creates its own V-HACD instance, so concurrent calls are safe.
 */
#ifndef VHACD_CAPI_H
#define VHACD_CAPI_H

#ifdef _WIN32
#define VHACD_NET_API __declspec(dllexport)
#else
#define VHACD_NET_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Bumped whenever a signature or struct layout changes. */
#define VHACD_NET_CAPI_VERSION 1

typedef struct vhacd_net_params {
    int struct_size;                    /* sizeof(vhacd_net_params) */
    unsigned max_convex_hulls;          /* default 64 */
    unsigned resolution;                /* voxel count, default 400000 */
    double min_volume_percent_error;    /* default 1.0 */
    unsigned max_recursion_depth;       /* default 10 */
    int shrink_wrap;                    /* default 1 */
    int fill_mode;                      /* 0 flood, 1 surface only, 2 raycast */
    unsigned max_vertices_per_hull;     /* default 64 */
    int multithreaded;                  /* upstream m_asyncACD: internal thread pool */
    unsigned min_edge_length;           /* default 2 */
    int find_best_plane;                /* default 0 */
} vhacd_net_params;

typedef struct vhacd_net_result vhacd_net_result;

VHACD_NET_API int vhacd_net_capi_version(void);

/* Returns 0 on success; on failure fills err (UTF-8, NUL-terminated). */
VHACD_NET_API int vhacd_net_run(const float* vertices, int vertex_count,
                                const int* triangles, int triangle_count,
                                const vhacd_net_params* params,
                                vhacd_net_result** result,
                                char* err, int err_len);

VHACD_NET_API int vhacd_net_result_count(const vhacd_net_result* result);

/* Borrowed pointers, valid until vhacd_net_result_free. */
VHACD_NET_API int vhacd_net_result_hull(const vhacd_net_result* result, int index,
                                        const float** vertices, int* vertex_count,
                                        const int** triangles, int* triangle_count);

VHACD_NET_API void vhacd_net_result_free(vhacd_net_result* result);

#ifdef __cplusplus
}
#endif

#endif

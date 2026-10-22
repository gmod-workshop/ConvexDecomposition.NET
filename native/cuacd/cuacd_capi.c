// cuacd_capi.c — flat C ABI over the CuACD host layer (see cuacd_capi.h).
//
// Compiled together with the upstream csrc/*.c (everything except the CPython
// module.c) into one shared library. Upstream sources are used verbatim.

#include "cuacd_capi.h"

#include "heap.h"
#include "structs.h"
#include "lookahead.h"
#include "preprocess.h"
#include "test.h"
#include "error_codes.h"

#include <cuda.h>
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

struct cuacd_context {
    gpu_ctx_t gpu;
    char      error[1024];
};

struct cuacd_result {
    struct gpu_result r;
};

// ---------------------------------------------------------------------------
// Error helpers
// ---------------------------------------------------------------------------

static void set_error(cuacd_context* c, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(c->error, sizeof(c->error), fmt, ap);
    va_end(ap);
}

// Describe a non-zero return code that came without a message. Upstream
// returns either a CUresult or a KERR_* kernel error word.
static void describe_code(int rc, char* buf, size_t len) {
    const char* name = NULL;
    const char* desc = NULL;
    if (cuGetErrorName((CUresult)rc, &name) == CUDA_SUCCESS && name) {
        cuGetErrorString((CUresult)rc, &desc);
        snprintf(buf, len, "%s (%d): %s", name, rc, desc ? desc : "");
    } else if (rc > 0) {
        kerr_decode(rc, buf, (int)len);
    } else {
        snprintf(buf, len, "error code %d", rc);
    }
}

// Record the failure of `what`, preferring upstream's own message.
static int fail(cuacd_context* c, int rc, const char* what) {
    const char* msg = gpu_last_error(c->gpu);
    if (msg) {
        set_error(c, "%s failed: %s", what, msg);
    } else {
        char code[512];
        describe_code(rc, code, sizeof(code));
        set_error(c, "%s failed: %s", what, code);
    }
    return rc ? rc : -1;
}

// Bind the context's CUDA context to the calling thread for one call.
static int enter(cuacd_context* c) {
    if (!c || !c->gpu) return -1;
    c->error[0] = '\0';
    c->gpu->last_error[0] = '\0';
    CUresult r = cuCtxPushCurrent(c->gpu->cuda_ctx);
    if (r != CUDA_SUCCESS) {
        char code[512];
        describe_code((int)r, code, sizeof(code));
        set_error(c, "cuCtxPushCurrent failed: %s", code);
        return (int)r;
    }
    return 0;
}

static void leave(void) {
    CUcontext popped;
    cuCtxPopCurrent(&popped);
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

CUACD_API int cuacd_capi_version(void) { return CUACD_CAPI_VERSION; }

CUACD_API int cuacd_query_devices(int* out_count, char* err_buf, int err_buf_len) {
    if (!out_count) return -1;
    *out_count = 0;
    CUresult r = cuInit(0);
    int n = 0;
    if (r == CUDA_SUCCESS) r = cuDeviceGetCount(&n);
    if (r != CUDA_SUCCESS) {
        if (err_buf && err_buf_len > 0) {
            char code[512];
            describe_code((int)r, code, sizeof(code));
            snprintf(err_buf, (size_t)err_buf_len, "cuInit failed: %s", code);
        }
        return (int)r;
    }
    int usable = 0;
    for (int i = 0; i < n; i++) {
        CUdevice dev;
        int major = 0;
        if (cuDeviceGet(&dev, i) == CUDA_SUCCESS &&
            cuDeviceGetAttribute(&major, CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MAJOR, dev) == CUDA_SUCCESS &&
            major >= 7)
            usable++;
    }
    *out_count = usable;
    return 0;
}

CUACD_API void cuacd_set_fatbin(const void* data, size_t len) {
    gpu_set_fatbin(data, len);
}

CUACD_API int cuacd_create(int device, uint64_t pool_bytes, cuacd_context** out,
                           char* err_buf, int err_buf_len) {
    if (!out) return -1;
    *out = NULL;

    gpu_ctx_t g = NULL;
    int rc = gpu_init(&g, device, (size_t)pool_bytes);
    if (rc != 0) {
        if (err_buf && err_buf_len > 0) {
            const char* msg = g ? gpu_last_error(g) : NULL;
            if (msg) {
                snprintf(err_buf, (size_t)err_buf_len, "%s", msg);
            } else {
                char code[512];
                describe_code(rc, code, sizeof(code));
                snprintf(err_buf, (size_t)err_buf_len,
                         g ? "CUDA initialization failed: %s"
                           : "cuInit failed (is an NVIDIA driver installed?): %s",
                         code);
            }
        }
        // gpu_init leaves a context it created current; gpu_destroy pops it.
        if (g) gpu_destroy(g);
        return rc;
    }

    cuacd_context* c = (cuacd_context*)calloc(1, sizeof(*c));
    if (!c) {
        gpu_destroy(g);
        if (err_buf && err_buf_len > 0)
            snprintf(err_buf, (size_t)err_buf_len, "out of host memory");
        return -1;
    }
    c->gpu = g;

    // cuCtxCreate made the new context current on this thread; every API call
    // pushes it explicitly instead, so leave the thread as we found it.
    if (g->owns_context) leave();

    *out = c;
    return 0;
}

CUACD_API void cuacd_destroy(cuacd_context* c) {
    if (!c) return;
    if (c->gpu) {
        int owns = c->gpu->owns_context;
        int pushed = cuCtxPushCurrent(c->gpu->cuda_ctx) == CUDA_SUCCESS;
        gpu_destroy(c->gpu);  // cuCtxDestroy also pops an owned context
        if (pushed && !owns) leave();
    }
    free(c);
}

CUACD_API const char* cuacd_last_error(cuacd_context* c) {
    if (!c || c->error[0] == '\0') return NULL;
    return c->error;
}

CUACD_API int cuacd_device_name(cuacd_context* c, char* buf, int buf_len) {
    if (!c || !buf || buf_len <= 0) return -1;
    CUresult r = cuDeviceGetName(buf, buf_len, c->gpu->device);
    if (r != CUDA_SUCCESS) return fail(c, (int)r, "cuDeviceGetName");
    return 0;
}

CUACD_API size_t cuacd_pool_usage(cuacd_context* c) {
    if (enter(c) != 0) return 0;
    size_t used = gpu_pool_usage(c->gpu);
    leave();
    return used;
}

CUACD_API void cuacd_free(void* p) { free(p); }

// ---------------------------------------------------------------------------
// Convex hull (k-DOP prefilter + exact D&C), outward oriented
// ---------------------------------------------------------------------------

// Caller has entered the context.
static int hull_impl(cuacd_context* c, const float* pts, int n,
                     float** out_verts, int* out_nv,
                     int** out_tris, int* out_nt, float* out_volume) {
    *out_verts = NULL; *out_tris = NULL;
    *out_nv = 0; *out_nt = 0;
    if (out_volume) *out_volume = 0.0f;

    if (!pts || n < 4) {
        set_error(c, "convex hull needs at least 4 points (got %d)", n);
        return -1;
    }

    // Natural bounds (Euler): a hull of n points has <= n verts, <= 2n-4 tris.
    int max_hv = n;
    int max_ht = 2 * n - 4;
    int offsets[2] = { 0, n };
    float* hv  = (float*)malloc((size_t)max_hv * 3 * sizeof(float));
    int*   ht  = (int*)malloc((size_t)max_ht * 3 * sizeof(int));
    int hnv = 0, hnt = 0, herr = 0;
    float hvol = 0.0f;
    if (!hv || !ht) {
        free(hv); free(ht);
        set_error(c, "out of host memory for a %d-point hull", n);
        return -1;
    }

    int rc = gpu_kdop_hull(c->gpu, pts, n, offsets, 1, max_hv, max_ht,
                           hv, ht, &hnv, &hnt, &hvol, &herr);
    if (rc != 0) {
        free(hv); free(ht);
        return fail(c, rc, "convex hull");
    }
    if (herr != 0) {
        char buf[512];
        kerr_decode(herr, buf, sizeof(buf));
        free(hv); free(ht);
        set_error(c, "convex hull kernel failed: %s", buf);
        return herr;
    }
    if (hnt <= 0 || hnv < 4) {
        free(hv); free(ht);
        set_error(c, "convex hull is degenerate (all points are coplanar or collinear)");
        return -1;
    }

    // Orient every face away from the hull centroid. The D&C hull is already
    // consistent in practice; this guarantees what lookahead_decompose
    // expects (the Python package does the same for scipy's hull).
    double cx = 0, cy = 0, cz = 0;
    for (int i = 0; i < hnv; i++) {
        cx += hv[3 * i]; cy += hv[3 * i + 1]; cz += hv[3 * i + 2];
    }
    cx /= hnv; cy /= hnv; cz /= hnv;
    double vol6 = 0, extent = 0;
    for (int t = 0; t < hnt; t++) {
        int* f = &ht[3 * t];
        const float* a = &hv[3 * f[0]];
        const float* b = &hv[3 * f[1]];
        const float* d = &hv[3 * f[2]];
        double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
        double vx = d[0] - a[0], vy = d[1] - a[1], vz = d[2] - a[2];
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double h = nx * (a[0] - cx) + ny * (a[1] - cy) + nz * (a[2] - cz);
        if (h < 0) {
            int tmp = f[1]; f[1] = f[2]; f[2] = tmp;
            h = -h;
        }
        vol6 += h;  // 6 × volume of the tetrahedron (centroid, face)
        for (int k = 0; k < 3; k++) {
            double e = fabs(hv[3 * f[k]] - cx) + fabs(hv[3 * f[k] + 1] - cy) + fabs(hv[3 * f[k] + 2] - cz);
            if (e > extent) extent = e;
        }
    }
    // A flat (coplanar) point set still yields faces, but no volume.
    if (vol6 <= 1e-9 * extent * extent * extent) {
        free(hv); free(ht);
        set_error(c, "convex hull is degenerate (all points are coplanar or collinear)");
        return -1;
    }

    float* hv_fit = (float*)realloc(hv, (size_t)hnv * 3 * sizeof(float));
    int*   ht_fit = (int*)realloc(ht, (size_t)hnt * 3 * sizeof(int));
    *out_verts = hv_fit ? hv_fit : hv;
    *out_tris  = ht_fit ? ht_fit : ht;
    *out_nv = hnv;
    *out_nt = hnt;
    if (out_volume) *out_volume = (float)(vol6 / 6.0);
    return 0;
}

CUACD_API int cuacd_convex_hull(cuacd_context* c, const float* pts, int n,
                                float** out_verts, int* out_nv,
                                int** out_tris, int* out_nt,
                                float* out_volume) {
    if (!out_verts || !out_nv || !out_tris || !out_nt) return -1;
    int rc = enter(c);
    if (rc != 0) return rc;
    rc = hull_impl(c, pts, n, out_verts, out_nv, out_tris, out_nt, out_volume);
    leave();
    return rc;
}

// ---------------------------------------------------------------------------
// Decomposition
// ---------------------------------------------------------------------------

CUACD_API int cuacd_decompose(cuacd_context* c,
                              const float* verts, int nv,
                              const int* tris, int nt,
                              const cuacd_decompose_params* p,
                              cuacd_result** out) {
    if (!out) return -1;
    *out = NULL;
    int rc = enter(c);
    if (rc != 0) return rc;

    if (!p || p->struct_size != (int)sizeof(cuacd_decompose_params)) {
        set_error(c, "cuacd_decompose_params size mismatch (managed/native version skew?)");
        leave();
        return -1;
    }
    if (!verts || !tris || nv < 4 || nt < 1) {
        set_error(c, "mesh needs at least 4 vertices and 1 triangle (got %d, %d)", nv, nt);
        leave();
        return -1;
    }
    // la_evaluate keeps its candidates in a fixed 512-entry shared buffer.
    if (p->width + 4 * p->n_concave_edges >= 512) {
        set_error(c, "width + 4*n_concave_edges must be < 512 (got %d)",
                  p->width + 4 * p->n_concave_edges);
        leave();
        return -1;
    }

    float* hv = NULL;
    int*   ht = NULL;
    int hnv = 0, hnt = 0;
    rc = hull_impl(c, verts, nv, &hv, &hnv, &ht, &hnt, NULL);
    if (rc != 0) {
        leave();
        return rc;
    }

    cuacd_result* res = (cuacd_result*)calloc(1, sizeof(*res));
    if (!res) {
        free(hv); free(ht);
        set_error(c, "out of host memory");
        leave();
        return -1;
    }

    rc = lookahead_decompose(c->gpu,
        verts, nv, tris, nt,
        hv, hnv, ht, hnt,
        p->max_iters, p->width, p->width2, p->threshold,
        p->depth, p->quick_depth, p->max_n_cutting,
        p->verbose, /*debug=*/0, p->decompose_components,
        p->decompose_components_per_iter,
        p->n_concave_edges, p->concave_eps, p->concave_threshold, p->concave_iters,
        p->merge_hulls,
        &res->r);
    free(hv);
    free(ht);

    if (rc != 0) {
        gpu_result_free(&res->r);
        free(res);
        rc = fail(c, rc, "lookahead_decompose");
        leave();
        return rc;
    }

    leave();
    *out = res;
    return 0;
}

CUACD_API int cuacd_result_count(const cuacd_result* res) {
    return res ? res->r.nparts : 0;
}

CUACD_API int cuacd_result_part(const cuacd_result* res, int index, cuacd_part_view* out) {
    if (!res || !out || index < 0 || index >= res->r.nparts) return -1;
    const struct gpu_part_result* p = &res->r.parts[index];
    out->verts      = p->verts;
    out->tris       = p->tris;
    out->nv         = p->nv;
    out->nt         = p->nt;
    out->hull_verts = p->hull_verts;
    out->hull_tris  = p->hull_tris;
    out->hull_nv    = p->hull_nv;
    out->hull_nt    = p->hull_nt;
    out->mesh_vol   = p->mesh_vol;
    out->hull_vol   = p->hull_vol;
    return 0;
}

CUACD_API void cuacd_result_free(cuacd_result* res) {
    if (!res) return;
    gpu_result_free(&res->r);
    free(res);
}

// ---------------------------------------------------------------------------
// Mesh audit / preprocess
// ---------------------------------------------------------------------------

CUACD_API int cuacd_check_mesh(cuacd_context* c,
                               const float* verts, int nv,
                               const int* tris, int nt,
                               uint32_t* out_flags) {
    if (!out_flags) return -1;
    *out_flags = 0;
    int rc = enter(c);
    if (rc != 0) return rc;
    unsigned int flags = 0;
    rc = gpu_mesh_audit(c->gpu, verts, nv, tris, nt, &flags);
    if (rc != 0) rc = fail(c, rc, "mesh audit");
    else *out_flags = flags;
    leave();
    return rc;
}

CUACD_API int cuacd_preprocess(cuacd_context* c,
                               const float* verts, int nv,
                               const int* tris, int nt,
                               int resolution,
                               float** out_verts, int* out_nv,
                               int** out_tris, int* out_nt) {
    if (!out_verts || !out_nv || !out_tris || !out_nt) return -1;
    *out_verts = NULL; *out_tris = NULL;
    *out_nv = 0; *out_nt = 0;
    int rc = enter(c);
    if (rc != 0) return rc;
    if (resolution < 8 || (resolution & (resolution - 1)) != 0) {
        set_error(c, "preprocess resolution must be a power of two >= 8 (got %d)", resolution);
        leave();
        return -1;
    }
    rc = gpu_preprocess(c->gpu, verts, nv, tris, nt, resolution,
                        out_verts, out_nv, out_tris, out_nt);
    if (rc != 0) {
        free(*out_verts); free(*out_tris);
        *out_verts = NULL; *out_tris = NULL;
        rc = fail(c, rc, "preprocess");
    }
    leave();
    return rc;
}

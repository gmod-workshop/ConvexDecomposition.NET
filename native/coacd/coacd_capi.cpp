// coacd_capi.cpp — exception-safe flat C ABI over CoACD (see coacd_capi.h).
// CoACD itself is linked statically from the pinned external/coacd submodule.

#include "coacd_capi.h"

#include <coacd.h>

#include <cstdio>
#include <exception>
#include <new>
#include <string>
#include <vector>

struct coacd_net_result {
    std::vector<std::vector<float>> verts;
    std::vector<std::vector<int>>   tris;
};

namespace {

void write_error(char* buf, int len, const char* msg) {
    if (buf && len > 0) std::snprintf(buf, static_cast<size_t>(len), "%s", msg);
}

// Run f, translating any C++ exception into an error code + message.
template <typename F>
int guarded(char* err, int err_len, F&& f) {
    try {
        return f();
    } catch (const std::bad_alloc&) {
        write_error(err, err_len, "CoACD ran out of memory");
    } catch (const std::exception& e) {
        write_error(err, err_len, e.what());
    } catch (...) {
        write_error(err, err_len, "CoACD failed with an unknown exception");
    }
    return -1;
}

} // namespace

extern "C" {

COACD_NET_API int coacd_net_capi_version(void) { return COACD_NET_CAPI_VERSION; }

COACD_NET_API int coacd_net_set_log_level(const char* level, char* err, int err_len) {
    if (!level) {
        write_error(err, err_len, "log level is null");
        return -1;
    }
    return guarded(err, err_len, [&] {
        coacd::set_log_level(level);
        return 0;
    });
}

COACD_NET_API int coacd_net_run(const float* verts, int nv,
                                const int* tris, int nt,
                                const coacd_net_params* p,
                                coacd_net_result** out,
                                char* err, int err_len) {
    if (!out) return -1;
    *out = nullptr;
    if (!p || p->struct_size != static_cast<int>(sizeof(coacd_net_params))) {
        write_error(err, err_len, "coacd_net_params size mismatch (managed/native version skew?)");
        return -1;
    }
    if (!verts || !tris || nv < 4 || nt < 1) {
        write_error(err, err_len, "mesh needs at least 4 vertices and 1 triangle");
        return -1;
    }

    return guarded(err, err_len, [&] {
        coacd::Mesh mesh;
        mesh.vertices.resize(static_cast<size_t>(nv));
        for (int i = 0; i < nv; i++)
            mesh.vertices[i] = { verts[3 * i], verts[3 * i + 1], verts[3 * i + 2] };
        mesh.indices.resize(static_cast<size_t>(nt));
        for (int i = 0; i < nt; i++)
            mesh.indices[i] = { tris[3 * i], tris[3 * i + 1], tris[3 * i + 2] };

        const char* preprocess = p->preprocess_mode == 1 ? "on"
                               : p->preprocess_mode == 2 ? "off" : "auto";
        const char* apx = p->apx_mode == 1 ? "box" : "ch";

        std::vector<coacd::Mesh> parts = coacd::CoACD(
            mesh, p->threshold, p->max_convex_hull, preprocess,
            p->prep_resolution, p->sample_resolution,
            p->mcts_nodes, p->mcts_iteration, p->mcts_max_depth,
            p->pca != 0, p->merge != 0, p->decimate != 0, p->max_ch_vertex,
            p->extrude != 0, p->extrude_margin, apx, p->seed, p->real_metric != 0);

        auto* res = new coacd_net_result();
        res->verts.resize(parts.size());
        res->tris.resize(parts.size());
        for (size_t i = 0; i < parts.size(); i++) {
            auto& v = res->verts[i];
            v.reserve(parts[i].vertices.size() * 3);
            for (const auto& pt : parts[i].vertices) {
                v.push_back(static_cast<float>(pt[0]));
                v.push_back(static_cast<float>(pt[1]));
                v.push_back(static_cast<float>(pt[2]));
            }
            auto& t = res->tris[i];
            t.reserve(parts[i].indices.size() * 3);
            for (const auto& tri : parts[i].indices) {
                t.push_back(tri[0]);
                t.push_back(tri[1]);
                t.push_back(tri[2]);
            }
        }
        *out = res;
        return 0;
    });
}

COACD_NET_API int coacd_net_result_count(const coacd_net_result* res) {
    return res ? static_cast<int>(res->verts.size()) : 0;
}

COACD_NET_API int coacd_net_result_hull(const coacd_net_result* res, int index,
                                        const float** verts, int* nv,
                                        const int** tris, int* nt) {
    if (!res || !verts || !nv || !tris || !nt ||
        index < 0 || index >= static_cast<int>(res->verts.size()))
        return -1;
    *verts = res->verts[index].data();
    *nv    = static_cast<int>(res->verts[index].size() / 3);
    *tris  = res->tris[index].data();
    *nt    = static_cast<int>(res->tris[index].size() / 3);
    return 0;
}

COACD_NET_API void coacd_net_result_free(coacd_net_result* res) {
    delete res;
}

} // extern "C"

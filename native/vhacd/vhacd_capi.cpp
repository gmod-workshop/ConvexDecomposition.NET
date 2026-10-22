#define ENABLE_VHACD_IMPLEMENTATION 1
#include "VHACD.h"

#include "vhacd_capi.h"

#include <cstdio>
#include <cstring>
#include <exception>
#include <new>
#include <vector>

struct vhacd_net_hull {
    std::vector<float> vertices;
    std::vector<int> triangles;
};

struct vhacd_net_result {
    std::vector<vhacd_net_hull> hulls;
};

namespace {

void set_error(char* err, int err_len, const char* msg)
{
    if (err && err_len > 0)
        std::snprintf(err, static_cast<size_t>(err_len), "%s", msg);
}

// Releases the IVHACD instance on every path.
struct Instance {
    VHACD::IVHACD* p = VHACD::CreateVHACD();
    ~Instance()
    {
        if (p)
            p->Release();
    }
};

} // namespace

extern "C" {

int vhacd_net_capi_version(void)
{
    return VHACD_NET_CAPI_VERSION;
}

int vhacd_net_run(const float* vertices, int vertex_count,
                  const int* triangles, int triangle_count,
                  const vhacd_net_params* params,
                  vhacd_net_result** result,
                  char* err, int err_len)
{
    if (result)
        *result = nullptr;
    if (!vertices || !triangles || !params || !result || vertex_count < 4 || triangle_count < 1) {
        set_error(err, err_len, "Invalid arguments.");
        return 1;
    }
    if (params->struct_size != static_cast<int>(sizeof(vhacd_net_params))) {
        set_error(err, err_len, "vhacd_net_params size mismatch.");
        return 1;
    }

    try {
        VHACD::IVHACD::Parameters p;
        p.m_maxConvexHulls = params->max_convex_hulls;
        p.m_resolution = params->resolution;
        p.m_minimumVolumePercentErrorAllowed = params->min_volume_percent_error;
        p.m_maxRecursionDepth = params->max_recursion_depth;
        p.m_shrinkWrap = params->shrink_wrap != 0;
        p.m_fillMode = params->fill_mode == 1 ? VHACD::FillMode::SURFACE_ONLY
                     : params->fill_mode == 2 ? VHACD::FillMode::RAYCAST_FILL
                                              : VHACD::FillMode::FLOOD_FILL;
        p.m_maxNumVerticesPerCH = params->max_vertices_per_hull;
        p.m_asyncACD = params->multithreaded != 0;
        p.m_minEdgeLength = params->min_edge_length;
        p.m_findBestPlane = params->find_best_plane != 0;

        Instance vhacd;
        if (!vhacd.p) {
            set_error(err, err_len, "Failed to create V-HACD instance.");
            return 1;
        }
        // Indices were range-checked by the caller, so the uint32 view is safe.
        if (!vhacd.p->Compute(vertices, static_cast<uint32_t>(vertex_count),
                              reinterpret_cast<const uint32_t*>(triangles),
                              static_cast<uint32_t>(triangle_count), p)) {
            set_error(err, err_len, "V-HACD failed to decompose the mesh.");
            return 1;
        }

        auto* out = new vhacd_net_result();
        uint32_t count = vhacd.p->GetNConvexHulls();
        out->hulls.resize(count);
        for (uint32_t i = 0; i < count; i++) {
            VHACD::IVHACD::ConvexHull ch;
            if (!vhacd.p->GetConvexHull(i, ch))
                continue;
            vhacd_net_hull& h = out->hulls[i];
            h.vertices.reserve(ch.m_points.size() * 3);
            for (const VHACD::Vertex& v : ch.m_points) {
                h.vertices.push_back(static_cast<float>(v.mX));
                h.vertices.push_back(static_cast<float>(v.mY));
                h.vertices.push_back(static_cast<float>(v.mZ));
            }
            h.triangles.reserve(ch.m_triangles.size() * 3);
            for (const VHACD::Triangle& t : ch.m_triangles) {
                h.triangles.push_back(static_cast<int>(t.mI0));
                h.triangles.push_back(static_cast<int>(t.mI1));
                h.triangles.push_back(static_cast<int>(t.mI2));
            }
        }
        *result = out;
        return 0;
    } catch (const std::bad_alloc&) {
        set_error(err, err_len, "V-HACD ran out of memory.");
    } catch (const std::exception& e) {
        set_error(err, err_len, e.what());
    } catch (...) {
        set_error(err, err_len, "V-HACD threw an unknown exception.");
    }
    return 1;
}

int vhacd_net_result_count(const vhacd_net_result* result)
{
    return result ? static_cast<int>(result->hulls.size()) : 0;
}

int vhacd_net_result_hull(const vhacd_net_result* result, int index,
                          const float** vertices, int* vertex_count,
                          const int** triangles, int* triangle_count)
{
    if (!result || index < 0 || index >= static_cast<int>(result->hulls.size()) ||
        !vertices || !vertex_count || !triangles || !triangle_count)
        return 1;
    const vhacd_net_hull& h = result->hulls[static_cast<size_t>(index)];
    *vertices = h.vertices.data();
    *vertex_count = static_cast<int>(h.vertices.size() / 3);
    *triangles = h.triangles.data();
    *triangle_count = static_cast<int>(h.triangles.size() / 3);
    return 0;
}

void vhacd_net_result_free(vhacd_net_result* result)
{
    delete result;
}

} // extern "C"

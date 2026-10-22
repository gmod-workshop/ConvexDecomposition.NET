using System;
using System.Collections.Generic;
using System.Numerics;

namespace ConvexDecomposition;

/// <summary>
/// A symmetric 3×3 inertia tensor. Off-diagonal entries are the products of
/// inertia with the conventional sign, e.g. <c>Ixy = −∫ρ·x·y dV</c>.
/// </summary>
public readonly record struct InertiaTensor(float Ixx, float Iyy, float Izz, float Ixy, float Ixz, float Iyz)
{
    /// <summary>The tensor as the upper-left 3×3 block of a matrix (the rest is identity).</summary>
    public Matrix4x4 ToMatrix() => new(
        Ixx, Ixy, Ixz, 0,
        Ixy, Iyy, Iyz, 0,
        Ixz, Iyz, Izz, 0,
        0, 0, 0, 1);
}

/// <summary>Volume, mass, center of mass and inertia of a solid of uniform density.</summary>
/// <param name="Volume">Enclosed volume.</param>
/// <param name="Mass">Volume × density.</param>
/// <param name="CenterOfMass">Centroid of the solid.</param>
/// <param name="Inertia">Inertia tensor about <paramref name="CenterOfMass"/>, for <paramref name="Mass"/>.</param>
public readonly record struct MassProperties(float Volume, float Mass, Vector3 CenterOfMass, InertiaTensor Inertia)
{
    /// <summary>
    /// Mass properties of a closed, outward-wound triangle mesh (Eberly,
    /// "Polyhedral Mass Properties (Revisited)").
    /// </summary>
    /// <param name="mesh">A closed mesh, such as a convex hull.</param>
    /// <param name="density">Mass per unit volume.</param>
    /// <exception cref="ArgumentException">The mesh encloses no volume.</exception>
    public static MassProperties Compute(Mesh mesh, float density = 1f)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        Vector3[] v = mesh.Vertices;
        int[] t = mesh.Indices;
        if (v.Length == 0 || t.Length == 0)
            throw new ArgumentException("The mesh is empty.", nameof(mesh));

        // Integrate relative to the bounding-box center to avoid cancellation.
        Vector3 lo = v[0], hi = v[0];
        foreach (Vector3 p in v)
        {
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }
        Vector3 origin = (lo + hi) * 0.5f;

        Span<double> s = stackalloc double[10];
        s.Clear();
        for (int i = 0; i < t.Length; i += 3)
        {
            Vector3 p0 = v[t[i]] - origin, p1 = v[t[i + 1]] - origin, p2 = v[t[i + 2]] - origin;
            double x0 = p0.X, y0 = p0.Y, z0 = p0.Z;
            double x1 = p1.X, y1 = p1.Y, z1 = p1.Z;
            double x2 = p2.X, y2 = p2.Y, z2 = p2.Z;
            double a1 = x1 - x0, b1 = y1 - y0, c1 = z1 - z0;
            double a2 = x2 - x0, b2 = y2 - y0, c2 = z2 - z0;
            double d0 = b1 * c2 - b2 * c1, d1 = a2 * c1 - a1 * c2, d2 = a1 * b2 - a2 * b1;

            Sub(x0, x1, x2, out double f1x, out double f2x, out double f3x, out double g0x, out double g1x, out double g2x);
            Sub(y0, y1, y2, out _, out double f2y, out double f3y, out double g0y, out double g1y, out double g2y);
            Sub(z0, z1, z2, out _, out double f2z, out double f3z, out double g0z, out double g1z, out double g2z);

            s[0] += d0 * f1x;
            s[1] += d0 * f2x;
            s[2] += d1 * f2y;
            s[3] += d2 * f2z;
            s[4] += d0 * f3x;
            s[5] += d1 * f3y;
            s[6] += d2 * f3z;
            s[7] += d0 * (y0 * g0x + y1 * g1x + y2 * g2x);
            s[8] += d1 * (z0 * g0y + z1 * g1y + z2 * g2y);
            s[9] += d2 * (x0 * g0z + x1 * g1z + x2 * g2z);
        }

        double volume = s[0] / 6;
        if (!(Math.Abs(volume) > 0))
            throw new ArgumentException("The mesh encloses no volume.", nameof(mesh));
        // An inside-out mesh integrates to negative values throughout.
        double sign = Math.Sign(volume);
        volume *= sign;
        double ix = sign * s[1] / 24, iy = sign * s[2] / 24, iz = sign * s[3] / 24;
        double ixx = sign * s[4] / 60, iyy = sign * s[5] / 60, izz = sign * s[6] / 60;
        double ixy = sign * s[7] / 120, iyz = sign * s[8] / 120, izx = sign * s[9] / 120;

        double cx = ix / volume, cy = iy / volume, cz = iz / volume;
        double rho = density;
        var inertia = new InertiaTensor(
            (float)(rho * (iyy + izz - volume * (cy * cy + cz * cz))),
            (float)(rho * (izz + ixx - volume * (cz * cz + cx * cx))),
            (float)(rho * (ixx + iyy - volume * (cx * cx + cy * cy))),
            (float)(-rho * (ixy - volume * cx * cy)),
            (float)(-rho * (izx - volume * cz * cx)),
            (float)(-rho * (iyz - volume * cy * cz)));
        return new MassProperties((float)volume, (float)(volume * rho),
            origin + new Vector3((float)cx, (float)cy, (float)cz), inertia);

        static void Sub(double w0, double w1, double w2,
            out double f1, out double f2, out double f3, out double g0, out double g1, out double g2)
        {
            double temp0 = w0 + w1;
            f1 = temp0 + w2;
            double temp1 = w0 * w0;
            double temp2 = temp1 + w1 * temp0;
            f2 = temp2 + w2 * f1;
            f3 = w0 * temp1 + w1 * temp2 + w2 * f2;
            g0 = f2 + w0 * (f1 + w0);
            g1 = f2 + w1 * (f1 + w1);
            g2 = f2 + w2 * (f1 + w2);
        }
    }

    /// <summary>
    /// Combines several bodies into one (parallel axis theorem), e.g. the hulls
    /// of a decomposition. Overlapping hulls count their shared volume twice.
    /// </summary>
    /// <exception cref="ArgumentException">No parts, or zero total mass.</exception>
    public static MassProperties Combine(IEnumerable<MassProperties> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var list = new List<MassProperties>(parts);
        double mass = 0, volume = 0;
        double cx = 0, cy = 0, cz = 0;
        foreach (MassProperties p in list)
        {
            mass += p.Mass;
            volume += p.Volume;
            cx += p.Mass * (double)p.CenterOfMass.X;
            cy += p.Mass * (double)p.CenterOfMass.Y;
            cz += p.Mass * (double)p.CenterOfMass.Z;
        }
        if (!(mass > 0))
            throw new ArgumentException("The parts have no total mass.", nameof(parts));
        cx /= mass;
        cy /= mass;
        cz /= mass;

        double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
        foreach (MassProperties p in list)
        {
            double dx = p.CenterOfMass.X - cx, dy = p.CenterOfMass.Y - cy, dz = p.CenterOfMass.Z - cz, m = p.Mass;
            xx += p.Inertia.Ixx + m * (dy * dy + dz * dz);
            yy += p.Inertia.Iyy + m * (dx * dx + dz * dz);
            zz += p.Inertia.Izz + m * (dx * dx + dy * dy);
            xy += p.Inertia.Ixy - m * dx * dy;
            xz += p.Inertia.Ixz - m * dx * dz;
            yz += p.Inertia.Iyz - m * dy * dz;
        }
        return new MassProperties((float)volume, (float)mass, new Vector3((float)cx, (float)cy, (float)cz),
            new InertiaTensor((float)xx, (float)yy, (float)zz, (float)xy, (float)xz, (float)yz));
    }
}

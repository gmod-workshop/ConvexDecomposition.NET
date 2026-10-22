using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ConvexDecomposition.Geometry;

/// <summary>Double-precision 3-vector for internal geometry.</summary>
internal readonly struct Double3
{
    public readonly double X, Y, Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Double3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public Double3(Vector3 v) : this(v.X, v.Y, v.Z) { }

    public static Double3 operator +(Double3 a, Double3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Double3 operator -(Double3 a, Double3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Double3 operator -(Double3 a) => new(-a.X, -a.Y, -a.Z);
    public static Double3 operator *(Double3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Double3 operator /(Double3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Dot(Double3 a, Double3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Double3 Cross(Double3 a, Double3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static Double3 Min(Double3 a, Double3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static Double3 Max(Double3 a, Double3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    public double LengthSquared => X * X + Y * Y + Z * Z;
    public double Length => Math.Sqrt(LengthSquared);

    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
}

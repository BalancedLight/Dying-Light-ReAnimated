namespace ReAnimated.Core.Mathematics;

/// <summary>
/// A deterministic, double-precision vector used by the authoring pipeline.
/// </summary>
public readonly record struct Vector3D(double X, double Y, double Z)
{
    public static Vector3D Zero => new(0.0, 0.0, 0.0);

    public static Vector3D One => new(1.0, 1.0, 1.0);

    public static Vector3D UnitX => new(1.0, 0.0, 0.0);

    public static Vector3D UnitY => new(0.0, 1.0, 0.0);

    public static Vector3D UnitZ => new(0.0, 0.0, 1.0);

    public double LengthSquared => Dot(this, this);

    public double Length => Math.Sqrt(LengthSquared);

    public bool IsFinite =>
        double.IsFinite(X) &&
        double.IsFinite(Y) &&
        double.IsFinite(Z);

    public Vector3D Normalized(double epsilon = 1e-12)
    {
        double length = Length;
        if (!double.IsFinite(length) || length <= epsilon)
        {
            throw new InvalidOperationException("A zero-length or non-finite vector cannot be normalized.");
        }

        return this / length;
    }

    public bool TryNormalize(out Vector3D normalized, double epsilon = 1e-12)
    {
        double length = Length;
        if (!double.IsFinite(length) || length <= epsilon)
        {
            normalized = Zero;
            return false;
        }

        normalized = this / length;
        return true;
    }

    public static double Dot(Vector3D left, Vector3D right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    public static Vector3D Cross(Vector3D left, Vector3D right) =>
        new(
            (left.Y * right.Z) - (left.Z * right.Y),
            (left.Z * right.X) - (left.X * right.Z),
            (left.X * right.Y) - (left.Y * right.X));

    public static Vector3D Lerp(Vector3D from, Vector3D to, double amount) =>
        from + ((to - from) * amount);

    public static double Distance(Vector3D left, Vector3D right) =>
        (left - right).Length;

    public static Vector3D ComponentMultiply(Vector3D left, Vector3D right) =>
        new(left.X * right.X, left.Y * right.Y, left.Z * right.Z);

    public static Vector3D operator +(Vector3D left, Vector3D right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static Vector3D operator -(Vector3D left, Vector3D right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static Vector3D operator -(Vector3D value) =>
        new(-value.X, -value.Y, -value.Z);

    public static Vector3D operator *(Vector3D value, double scale) =>
        new(value.X * scale, value.Y * scale, value.Z * scale);

    public static Vector3D operator *(double scale, Vector3D value) => value * scale;

    public static Vector3D operator /(Vector3D value, double divisor)
    {
        if (divisor == 0.0)
        {
            throw new DivideByZeroException();
        }

        return new(value.X / divisor, value.Y / divisor, value.Z / divisor);
    }
}

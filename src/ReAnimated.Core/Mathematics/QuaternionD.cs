namespace ReAnimated.Core.Mathematics;

/// <summary>
/// A double-precision quaternion with an explicit XYZW component order.
/// </summary>
public readonly record struct QuaternionD(double X, double Y, double Z, double W)
{
    public static QuaternionD Identity => new(0.0, 0.0, 0.0, 1.0);

    public double LengthSquared =>
        (X * X) + (Y * Y) + (Z * Z) + (W * W);

    public bool IsFinite =>
        double.IsFinite(X) &&
        double.IsFinite(Y) &&
        double.IsFinite(Z) &&
        double.IsFinite(W);

    public QuaternionD Normalized(double epsilon = 1e-12)
    {
        double length = Math.Sqrt(LengthSquared);
        if (!double.IsFinite(length) || length <= epsilon)
        {
            throw new InvalidOperationException("A zero-length or non-finite quaternion cannot be normalized.");
        }

        return new(X / length, Y / length, Z / length, W / length);
    }

    public QuaternionD Conjugate() => new(-X, -Y, -Z, W);

    public QuaternionD Inverse(double epsilon = 1e-12)
    {
        double lengthSquared = LengthSquared;
        if (!double.IsFinite(lengthSquared) || lengthSquared <= epsilon)
        {
            throw new InvalidOperationException("A zero-length or non-finite quaternion cannot be inverted.");
        }

        QuaternionD conjugate = Conjugate();
        return new(
            conjugate.X / lengthSquared,
            conjugate.Y / lengthSquared,
            conjugate.Z / lengthSquared,
            conjugate.W / lengthSquared);
    }

    public Vector3D Rotate(Vector3D value)
    {
        QuaternionD unit = Normalized();
        Vector3D q = new(unit.X, unit.Y, unit.Z);
        Vector3D twiceCross = 2.0 * Vector3D.Cross(q, value);
        return value + (unit.W * twiceCross) + Vector3D.Cross(q, twiceCross);
    }

    public static double Dot(QuaternionD left, QuaternionD right) =>
        (left.X * right.X) +
        (left.Y * right.Y) +
        (left.Z * right.Z) +
        (left.W * right.W);

    public static QuaternionD FromAxisAngle(Vector3D axis, double radians)
    {
        if (!double.IsFinite(radians))
        {
            throw new ArgumentOutOfRangeException(nameof(radians), "The angle must be finite.");
        }

        Vector3D normalizedAxis = axis.Normalized();
        double halfAngle = radians * 0.5;
        double sin = Math.Sin(halfAngle);
        return new QuaternionD(
            normalizedAxis.X * sin,
            normalizedAxis.Y * sin,
            normalizedAxis.Z * sin,
            Math.Cos(halfAngle)).Normalized();
    }

    public static QuaternionD FromToRotation(
        Vector3D from,
        Vector3D to,
        double epsilon = 1e-10)
    {
        Vector3D fromUnit = from.Normalized(epsilon);
        Vector3D toUnit = to.Normalized(epsilon);
        double dot = Math.Clamp(Vector3D.Dot(fromUnit, toUnit), -1.0, 1.0);

        if (dot >= 1.0 - epsilon)
        {
            return Identity;
        }

        if (dot <= -1.0 + epsilon)
        {
            Vector3D candidate = Math.Abs(fromUnit.X) < 0.8
                ? Vector3D.UnitX
                : Vector3D.UnitY;
            Vector3D axis = Vector3D.Cross(fromUnit, candidate).Normalized(epsilon);
            return FromAxisAngle(axis, Math.PI);
        }

        Vector3D cross = Vector3D.Cross(fromUnit, toUnit);
        return new QuaternionD(cross.X, cross.Y, cross.Z, 1.0 + dot).Normalized();
    }

    public static QuaternionD Slerp(QuaternionD from, QuaternionD to, double amount)
    {
        if (!double.IsFinite(amount))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "The interpolation amount must be finite.");
        }

        QuaternionD fromUnit = from.Normalized();
        QuaternionD toUnit = to.Normalized();
        double dot = Dot(fromUnit, toUnit);

        if (dot < 0.0)
        {
            toUnit = -toUnit;
            dot = -dot;
        }

        dot = Math.Clamp(dot, -1.0, 1.0);
        if (dot > 0.9995)
        {
            return new QuaternionD(
                fromUnit.X + ((toUnit.X - fromUnit.X) * amount),
                fromUnit.Y + ((toUnit.Y - fromUnit.Y) * amount),
                fromUnit.Z + ((toUnit.Z - fromUnit.Z) * amount),
                fromUnit.W + ((toUnit.W - fromUnit.W) * amount)).Normalized();
        }

        double theta = Math.Acos(dot);
        double sinTheta = Math.Sin(theta);
        double fromWeight = Math.Sin((1.0 - amount) * theta) / sinTheta;
        double toWeight = Math.Sin(amount * theta) / sinTheta;

        return new(
            (fromUnit.X * fromWeight) + (toUnit.X * toWeight),
            (fromUnit.Y * fromWeight) + (toUnit.Y * toWeight),
            (fromUnit.Z * fromWeight) + (toUnit.Z * toWeight),
            (fromUnit.W * fromWeight) + (toUnit.W * toWeight));
    }

    public static QuaternionD FromRotationMatrix(TransformMatrix matrix)
    {
        double trace = matrix.M11 + matrix.M22 + matrix.M33;
        QuaternionD result;

        if (trace > 0.0)
        {
            double scale = Math.Sqrt(trace + 1.0) * 2.0;
            result = new(
                (matrix.M32 - matrix.M23) / scale,
                (matrix.M13 - matrix.M31) / scale,
                (matrix.M21 - matrix.M12) / scale,
                0.25 * scale);
        }
        else if (matrix.M11 > matrix.M22 && matrix.M11 > matrix.M33)
        {
            double scale = Math.Sqrt(1.0 + matrix.M11 - matrix.M22 - matrix.M33) * 2.0;
            result = new(
                0.25 * scale,
                (matrix.M12 + matrix.M21) / scale,
                (matrix.M13 + matrix.M31) / scale,
                (matrix.M32 - matrix.M23) / scale);
        }
        else if (matrix.M22 > matrix.M33)
        {
            double scale = Math.Sqrt(1.0 + matrix.M22 - matrix.M11 - matrix.M33) * 2.0;
            result = new(
                (matrix.M12 + matrix.M21) / scale,
                0.25 * scale,
                (matrix.M23 + matrix.M32) / scale,
                (matrix.M13 - matrix.M31) / scale);
        }
        else
        {
            double scale = Math.Sqrt(1.0 + matrix.M33 - matrix.M11 - matrix.M22) * 2.0;
            result = new(
                (matrix.M13 + matrix.M31) / scale,
                (matrix.M23 + matrix.M32) / scale,
                0.25 * scale,
                (matrix.M21 - matrix.M12) / scale);
        }

        return result.Normalized();
    }

    public static QuaternionD operator *(QuaternionD left, QuaternionD right) =>
        new(
            (left.W * right.X) + (left.X * right.W) + (left.Y * right.Z) - (left.Z * right.Y),
            (left.W * right.Y) - (left.X * right.Z) + (left.Y * right.W) + (left.Z * right.X),
            (left.W * right.Z) + (left.X * right.Y) - (left.Y * right.X) + (left.Z * right.W),
            (left.W * right.W) - (left.X * right.X) - (left.Y * right.Y) - (left.Z * right.Z));

    public static QuaternionD operator -(QuaternionD value) =>
        new(-value.X, -value.Y, -value.Z, -value.W);
}

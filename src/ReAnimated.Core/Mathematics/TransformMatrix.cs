namespace ReAnimated.Core.Mathematics;

/// <summary>
/// A double-precision 4x4 matrix using column vectors.
/// Translation is stored in M14/M24/M34 and hierarchy composition is
/// <c>global = parent * local</c>.
/// </summary>
public readonly record struct TransformMatrix(
    double M11,
    double M12,
    double M13,
    double M14,
    double M21,
    double M22,
    double M23,
    double M24,
    double M31,
    double M32,
    double M33,
    double M34,
    double M41,
    double M42,
    double M43,
    double M44)
{
    public static TransformMatrix Identity =>
        new(
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0);

    public Vector3D Translation => new(M14, M24, M34);

    public double LinearDeterminant =>
        (M11 * ((M22 * M33) - (M23 * M32))) -
        (M12 * ((M21 * M33) - (M23 * M31))) +
        (M13 * ((M21 * M32) - (M22 * M31)));

    public bool IsFinite =>
        double.IsFinite(M11) && double.IsFinite(M12) &&
        double.IsFinite(M13) && double.IsFinite(M14) &&
        double.IsFinite(M21) && double.IsFinite(M22) &&
        double.IsFinite(M23) && double.IsFinite(M24) &&
        double.IsFinite(M31) && double.IsFinite(M32) &&
        double.IsFinite(M33) && double.IsFinite(M34) &&
        double.IsFinite(M41) && double.IsFinite(M42) &&
        double.IsFinite(M43) && double.IsFinite(M44);

    public static TransformMatrix CreateTranslation(Vector3D translation) =>
        new(
            1.0, 0.0, 0.0, translation.X,
            0.0, 1.0, 0.0, translation.Y,
            0.0, 0.0, 1.0, translation.Z,
            0.0, 0.0, 0.0, 1.0);

    public static TransformMatrix CreateScale(Vector3D scale) =>
        new(
            scale.X, 0.0, 0.0, 0.0,
            0.0, scale.Y, 0.0, 0.0,
            0.0, 0.0, scale.Z, 0.0,
            0.0, 0.0, 0.0, 1.0);

    public static TransformMatrix CreateRotation(QuaternionD rotation)
    {
        QuaternionD unit = rotation.Normalized();
        double xx = unit.X * unit.X;
        double yy = unit.Y * unit.Y;
        double zz = unit.Z * unit.Z;
        double xy = unit.X * unit.Y;
        double xz = unit.X * unit.Z;
        double yz = unit.Y * unit.Z;
        double xw = unit.X * unit.W;
        double yw = unit.Y * unit.W;
        double zw = unit.Z * unit.W;

        return new(
            1.0 - (2.0 * (yy + zz)),
            2.0 * (xy - zw),
            2.0 * (xz + yw),
            0.0,
            2.0 * (xy + zw),
            1.0 - (2.0 * (xx + zz)),
            2.0 * (yz - xw),
            0.0,
            2.0 * (xz - yw),
            2.0 * (yz + xw),
            1.0 - (2.0 * (xx + yy)),
            0.0,
            0.0,
            0.0,
            0.0,
            1.0);
    }

    public static TransformMatrix FromTrs(TransformTRS transform) =>
        CreateTranslation(transform.Translation) *
        CreateRotation(transform.Rotation) *
        CreateScale(transform.Scale);

    public Vector3D TransformPoint(Vector3D point)
    {
        double x = (M11 * point.X) + (M12 * point.Y) + (M13 * point.Z) + M14;
        double y = (M21 * point.X) + (M22 * point.Y) + (M23 * point.Z) + M24;
        double z = (M31 * point.X) + (M32 * point.Y) + (M33 * point.Z) + M34;
        double w = (M41 * point.X) + (M42 * point.Y) + (M43 * point.Z) + M44;

        if (Math.Abs(w) <= 1e-12)
        {
            throw new InvalidOperationException("The matrix transformed the point to an invalid homogeneous coordinate.");
        }

        return Math.Abs(w - 1.0) <= 1e-12
            ? new(x, y, z)
            : new(x / w, y / w, z / w);
    }

    public Vector3D TransformDirection(Vector3D direction) =>
        new(
            (M11 * direction.X) + (M12 * direction.Y) + (M13 * direction.Z),
            (M21 * direction.X) + (M22 * direction.Y) + (M23 * direction.Z),
            (M31 * direction.X) + (M32 * direction.Y) + (M33 * direction.Z));

    public TransformMatrix InvertedAffine(double epsilon = 1e-12)
    {
        if (Math.Abs(M41) > epsilon ||
            Math.Abs(M42) > epsilon ||
            Math.Abs(M43) > epsilon ||
            Math.Abs(M44 - 1.0) > epsilon)
        {
            throw new InvalidOperationException("Only affine matrices can be inverted by the authoring transform contract.");
        }

        double determinant = LinearDeterminant;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) <= epsilon)
        {
            throw new InvalidOperationException("The affine matrix is singular and cannot be inverted.");
        }

        double inverseDeterminant = 1.0 / determinant;
        double i11 = ((M22 * M33) - (M23 * M32)) * inverseDeterminant;
        double i12 = ((M13 * M32) - (M12 * M33)) * inverseDeterminant;
        double i13 = ((M12 * M23) - (M13 * M22)) * inverseDeterminant;
        double i21 = ((M23 * M31) - (M21 * M33)) * inverseDeterminant;
        double i22 = ((M11 * M33) - (M13 * M31)) * inverseDeterminant;
        double i23 = ((M13 * M21) - (M11 * M23)) * inverseDeterminant;
        double i31 = ((M21 * M32) - (M22 * M31)) * inverseDeterminant;
        double i32 = ((M12 * M31) - (M11 * M32)) * inverseDeterminant;
        double i33 = ((M11 * M22) - (M12 * M21)) * inverseDeterminant;

        double i14 = -((i11 * M14) + (i12 * M24) + (i13 * M34));
        double i24 = -((i21 * M14) + (i22 * M24) + (i23 * M34));
        double i34 = -((i31 * M14) + (i32 * M24) + (i33 * M34));

        return new(
            i11, i12, i13, i14,
            i21, i22, i23, i24,
            i31, i32, i33, i34,
            0.0, 0.0, 0.0, 1.0);
    }

    public TransformTRS Decompose(double epsilon = 1e-10)
    {
        Vector3D columnX = new(M11, M21, M31);
        Vector3D columnY = new(M12, M22, M32);
        Vector3D columnZ = new(M13, M23, M33);

        double scaleX = columnX.Length;
        double scaleY = columnY.Length;
        double scaleZ = columnZ.Length;
        if (scaleX <= epsilon || scaleY <= epsilon || scaleZ <= epsilon)
        {
            throw new InvalidOperationException("A matrix with a zero scale axis cannot be decomposed.");
        }

        if (LinearDeterminant < 0.0)
        {
            scaleX = -scaleX;
        }

        Vector3D axisX = columnX / scaleX;
        Vector3D axisY = columnY / scaleY;
        Vector3D axisZ = columnZ / scaleZ;
        if (Math.Abs(Vector3D.Dot(axisX, axisY)) > epsilon ||
            Math.Abs(Vector3D.Dot(axisX, axisZ)) > epsilon ||
            Math.Abs(Vector3D.Dot(axisY, axisZ)) > epsilon)
        {
            throw new InvalidOperationException("A sheared matrix cannot be represented as translation, rotation, and scale.");
        }

        TransformMatrix rotation = new(
            axisX.X, axisY.X, axisZ.X, 0.0,
            axisX.Y, axisY.Y, axisZ.Y, 0.0,
            axisX.Z, axisY.Z, axisZ.Z, 0.0,
            0.0, 0.0, 0.0, 1.0);

        return new(
            Translation,
            QuaternionD.FromRotationMatrix(rotation),
            new(scaleX, scaleY, scaleZ));
    }

    public bool NearlyEquals(TransformMatrix other, double tolerance = 1e-9) =>
        Math.Abs(M11 - other.M11) <= tolerance &&
        Math.Abs(M12 - other.M12) <= tolerance &&
        Math.Abs(M13 - other.M13) <= tolerance &&
        Math.Abs(M14 - other.M14) <= tolerance &&
        Math.Abs(M21 - other.M21) <= tolerance &&
        Math.Abs(M22 - other.M22) <= tolerance &&
        Math.Abs(M23 - other.M23) <= tolerance &&
        Math.Abs(M24 - other.M24) <= tolerance &&
        Math.Abs(M31 - other.M31) <= tolerance &&
        Math.Abs(M32 - other.M32) <= tolerance &&
        Math.Abs(M33 - other.M33) <= tolerance &&
        Math.Abs(M34 - other.M34) <= tolerance &&
        Math.Abs(M41 - other.M41) <= tolerance &&
        Math.Abs(M42 - other.M42) <= tolerance &&
        Math.Abs(M43 - other.M43) <= tolerance &&
        Math.Abs(M44 - other.M44) <= tolerance;

    public static TransformMatrix operator *(TransformMatrix left, TransformMatrix right) =>
        new(
            (left.M11 * right.M11) + (left.M12 * right.M21) + (left.M13 * right.M31) + (left.M14 * right.M41),
            (left.M11 * right.M12) + (left.M12 * right.M22) + (left.M13 * right.M32) + (left.M14 * right.M42),
            (left.M11 * right.M13) + (left.M12 * right.M23) + (left.M13 * right.M33) + (left.M14 * right.M43),
            (left.M11 * right.M14) + (left.M12 * right.M24) + (left.M13 * right.M34) + (left.M14 * right.M44),
            (left.M21 * right.M11) + (left.M22 * right.M21) + (left.M23 * right.M31) + (left.M24 * right.M41),
            (left.M21 * right.M12) + (left.M22 * right.M22) + (left.M23 * right.M32) + (left.M24 * right.M42),
            (left.M21 * right.M13) + (left.M22 * right.M23) + (left.M23 * right.M33) + (left.M24 * right.M43),
            (left.M21 * right.M14) + (left.M22 * right.M24) + (left.M23 * right.M34) + (left.M24 * right.M44),
            (left.M31 * right.M11) + (left.M32 * right.M21) + (left.M33 * right.M31) + (left.M34 * right.M41),
            (left.M31 * right.M12) + (left.M32 * right.M22) + (left.M33 * right.M32) + (left.M34 * right.M42),
            (left.M31 * right.M13) + (left.M32 * right.M23) + (left.M33 * right.M33) + (left.M34 * right.M43),
            (left.M31 * right.M14) + (left.M32 * right.M24) + (left.M33 * right.M34) + (left.M34 * right.M44),
            (left.M41 * right.M11) + (left.M42 * right.M21) + (left.M43 * right.M31) + (left.M44 * right.M41),
            (left.M41 * right.M12) + (left.M42 * right.M22) + (left.M43 * right.M32) + (left.M44 * right.M42),
            (left.M41 * right.M13) + (left.M42 * right.M23) + (left.M43 * right.M33) + (left.M44 * right.M43),
            (left.M41 * right.M14) + (left.M42 * right.M24) + (left.M43 * right.M34) + (left.M44 * right.M44));
}

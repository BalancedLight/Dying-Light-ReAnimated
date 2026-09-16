using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

/// <summary>Generic pose and proportion controls for anatomical volume fixtures.</summary>
public sealed record AnatomicalFixtureOptions
{
    public AnatomicalFixturePose Pose { get; init; } = AnatomicalFixturePose.TPose;

    public bool BentElbows { get; init; }

    public bool BentKnees { get; init; }

    public bool UnequalSegmentProportions { get; init; }

    public double UniformScale { get; init; } = 1.0;

    public TransformMatrix RigidTransform { get; init; } = TransformMatrix.Identity;
}

public enum AnatomicalFixturePose
{
    TPose,
    APose,
}

/// <summary>
/// A closed synthetic source mesh and its construction-authored landmark
/// contract. Expected landmarks describe the fixture only; they are not
/// detector output or an anatomy inference result.
/// </summary>
public sealed record AnatomicalVolumeFixture(
    SourceGeometryAnalysis Geometry,
    ImmutableDictionary<string, Vector3D> ExpectedJointCoordinates,
    ImmutableArray<string> ExpectedJointRoles)
{
    public ImmutableArray<string> JointRoles => ExpectedJointRoles;
}

/// <summary>Creates generic closed articulated biped volumes for detector tests.</summary>
public static class AnatomicalVolumeFixtures
{
    private const int RingSegments = 12;
    private const int LatitudeSegments = 8;

    public static AnatomicalVolumeFixture Create(AnatomicalFixtureOptions? options = null)
    {
        options ??= new AnatomicalFixtureOptions();
        Validate(options);

        var mesh = new MeshBuilder(options.UniformScale, options.RigidTransform);
        Vector3D pelvis = new(0, 0, 0);
        Vector3D chest = new(0, 1.15, 0);
        Vector3D neck = new(0, 1.95, 0);
        Vector3D head = new(0, 2.35, 0);
        double legScale = options.UnequalSegmentProportions ? 1.15 : 1.0;
        double armScale = options.UnequalSegmentProportions ? 0.88 : 1.0;

        Vector3D leftShoulder = new(-0.58, 1.6, 0);
        Vector3D rightShoulder = new(0.58, 1.6, 0);
        Vector3D leftElbow = options.Pose == AnatomicalFixturePose.TPose
            ? new(-1.18 * armScale, 1.6, 0)
            : new(-1.08 * armScale, 1.25, 0);
        Vector3D rightElbow = options.Pose == AnatomicalFixturePose.TPose
            ? new(1.18 * armScale, 1.6, 0)
            : new(1.08 * armScale, 1.25, 0);
        Vector3D leftWrist = options.Pose == AnatomicalFixturePose.TPose
            ? new(-1.82 * armScale, 1.6, 0)
            : new(-1.53 * armScale, 0.75, 0);
        Vector3D rightWrist = options.Pose == AnatomicalFixturePose.TPose
            ? new(1.82 * armScale, 1.6, 0)
            : new(1.53 * armScale, 0.75, 0);

        if (options.BentElbows)
        {
            leftWrist = leftElbow + new Vector3D(-0.2, -0.45, 0.35) * armScale;
            rightWrist = rightElbow + new Vector3D(0.2, -0.45, -0.35) * armScale;
        }

        Vector3D leftHip = new(-0.28, -0.3, 0);
        Vector3D rightHip = new(0.28, -0.3, 0);
        Vector3D leftKnee = new(-0.28, -1.15 * legScale, 0);
        Vector3D rightKnee = new(0.28, -1.15 * legScale, 0);
        Vector3D leftAnkle = new(-0.28, -2.05 * legScale, 0);
        Vector3D rightAnkle = new(0.28, -2.05 * legScale, 0);
        if (options.BentKnees)
        {
            leftKnee += new Vector3D(-0.08, 0, 0.35);
            rightKnee += new Vector3D(0.08, 0, -0.35);
            leftAnkle += new Vector3D(0.12, 0, 0.25);
            rightAnkle += new Vector3D(-0.12, 0, -0.25);
        }

        mesh.AppendEllipsoid(pelvis, new(0.58, 0.42, 0.38));
        mesh.AppendEllipsoid(chest, new(0.66, 0.78, 0.4));
        mesh.AppendEllipsoid(neck, new(0.22, 0.3, 0.22));
        mesh.AppendEllipsoid(head, new(0.34, 0.42, 0.34));

        mesh.AppendRoundedLimb(leftShoulder, leftElbow, 0.21, 0.18);
        mesh.AppendRoundedLimb(rightShoulder, rightElbow, 0.21, 0.18);
        mesh.AppendRoundedLimb(leftElbow, leftWrist, 0.17, 0.12);
        mesh.AppendRoundedLimb(rightElbow, rightWrist, 0.17, 0.12);
        mesh.AppendRoundedLimb(leftHip, leftKnee, 0.28, 0.22);
        mesh.AppendRoundedLimb(rightHip, rightKnee, 0.28, 0.22);
        mesh.AppendRoundedLimb(leftKnee, leftAnkle, 0.21, 0.14);
        mesh.AppendRoundedLimb(rightKnee, rightAnkle, 0.21, 0.14);

        foreach (Vector3D joint in new[]
        {
            leftShoulder, rightShoulder, leftElbow, rightElbow,
            leftHip, rightHip, leftKnee, rightKnee,
        })
        {
            mesh.AppendEllipsoid(joint, new(0.25, 0.24, 0.23));
        }

        ImmutableArray<Vector3D> points = mesh.Points.ToImmutableArray();
        ImmutableArray<SourceGeometryAnalysisTriangle> triangles = mesh.Triangles
            .Select((triangle, index) => new SourceGeometryAnalysisTriangle(
                new GeometrySourceTriangle(index, 0), triangle.A, triangle.B, triangle.C))
            .ToImmutableArray();
        var component = new SourceGeometryComponentAnalysis(
            new GeometrySourceComponent("synthetic-anatomical-volume", points),
            triangles,
            SourceMeshTopology.Build(points.Length, mesh.Triangles));
        var analysis = new SourceGeometryAnalysis(
            new string('a', 64),
            [component]);

        var landmarks = new Dictionary<string, Vector3D>(StringComparer.Ordinal)
        {
            ["root"] = pelvis,
            ["pelvis"] = pelvis,
            ["chest"] = chest,
            ["neck"] = neck,
            ["head"] = head,
            ["left_shoulder"] = leftShoulder,
            ["right_shoulder"] = rightShoulder,
            ["left_elbow"] = leftElbow,
            ["right_elbow"] = rightElbow,
            ["left_wrist"] = leftWrist,
            ["right_wrist"] = rightWrist,
            ["left_hip"] = leftHip,
            ["right_hip"] = rightHip,
            ["left_knee"] = leftKnee,
            ["right_knee"] = rightKnee,
            ["left_ankle"] = leftAnkle,
            ["right_ankle"] = rightAnkle,
        };
        ImmutableDictionary<string, Vector3D> transformedLandmarks = landmarks
            .ToDictionary(
                pair => pair.Key,
                pair => mesh.Transform(pair.Value),
                StringComparer.Ordinal)
            .ToImmutableDictionary(StringComparer.Ordinal);
        ImmutableArray<string> roles = transformedLandmarks.Keys.Order(StringComparer.Ordinal).ToImmutableArray();
        return new(analysis, transformedLandmarks, roles);
    }

    private static void Validate(AnatomicalFixtureOptions options)
    {
        if (!double.IsFinite(options.UniformScale) || options.UniformScale <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Fixture scale must be finite and positive.");
        }
    }

    private sealed class MeshBuilder
    {
        private readonly double _scale;
        private readonly TransformMatrix _transform;
        public List<Vector3D> Points { get; } = [];
        public List<(int A, int B, int C)> Triangles { get; } = [];

        public MeshBuilder(double scale, TransformMatrix transform)
        {
            _scale = scale;
            _transform = transform;
        }

        public void AppendEllipsoid(Vector3D center, Vector3D radii)
        {
            int north = AddPoint(center + new Vector3D(0, radii.Y, 0));
            var rings = new int[LatitudeSegments - 1][];
            for (int latitude = 1; latitude < LatitudeSegments; latitude++)
            {
                double theta = Math.PI * latitude / LatitudeSegments;
                rings[latitude - 1] = new int[RingSegments];
                for (int longitude = 0; longitude < RingSegments; longitude++)
                {
                    double phi = 2.0 * Math.PI * longitude / RingSegments;
                    rings[latitude - 1][longitude] = AddPoint(center + new Vector3D(
                        radii.X * Math.Sin(theta) * Math.Cos(phi),
                        radii.Y * Math.Cos(theta),
                        radii.Z * Math.Sin(theta) * Math.Sin(phi)));
                }
            }

            int south = AddPoint(center - new Vector3D(0, radii.Y, 0));
            for (int longitude = 0; longitude < RingSegments; longitude++)
            {
                int next = (longitude + 1) % RingSegments;
                Triangles.Add((north, rings[0][next], rings[0][longitude]));
                Triangles.Add((south, rings[^1][longitude], rings[^1][next]));
            }

            for (int ring = 0; ring < rings.Length - 1; ring++)
            for (int longitude = 0; longitude < RingSegments; longitude++)
            {
                int next = (longitude + 1) % RingSegments;
                int a = rings[ring][longitude];
                int b = rings[ring][next];
                int c = rings[ring + 1][longitude];
                int d = rings[ring + 1][next];
                Triangles.Add((a, b, c));
                Triangles.Add((b, d, c));
            }
        }

        public void AppendRoundedLimb(Vector3D start, Vector3D end, double startRadius, double endRadius)
        {
            Vector3D axis = end - start;
            double length = axis.Length;
            if (!axis.TryNormalize(out Vector3D direction) || !double.IsFinite(length) || length <= 0)
            {
                throw new ArgumentException("Fixture limb endpoints must define a finite non-zero segment.");
            }

            Vector3D reference = Math.Abs(Vector3D.Dot(direction, Vector3D.UnitY)) < 0.9
                ? Vector3D.UnitY
                : Vector3D.UnitX;
            Vector3D side = Vector3D.Cross(direction, reference).Normalized();
            Vector3D forward = Vector3D.Cross(side, direction).Normalized();
            int north = AddPoint(start);
            var rings = new int[LatitudeSegments - 1][];
            for (int latitude = 1; latitude < LatitudeSegments; latitude++)
            {
                double theta = Math.PI * latitude / LatitudeSegments;
                double along = (1.0 - Math.Cos(theta)) * 0.5;
                double radius = startRadius + ((endRadius - startRadius) * along);
                Vector3D centre = start + (direction * (length * along));
                double ringRadius = radius * Math.Sin(theta);
                rings[latitude - 1] = new int[RingSegments];
                for (int longitude = 0; longitude < RingSegments; longitude++)
                {
                    double phi = 2.0 * Math.PI * longitude / RingSegments;
                    rings[latitude - 1][longitude] = AddPoint(
                        centre + (side * (ringRadius * Math.Cos(phi))) +
                        (forward * (ringRadius * Math.Sin(phi))));
                }
            }

            int south = AddPoint(end);
            for (int longitude = 0; longitude < RingSegments; longitude++)
            {
                int next = (longitude + 1) % RingSegments;
                Triangles.Add((north, rings[0][next], rings[0][longitude]));
                Triangles.Add((south, rings[^1][longitude], rings[^1][next]));
            }

            for (int ring = 0; ring < rings.Length - 1; ring++)
            for (int longitude = 0; longitude < RingSegments; longitude++)
            {
                int next = (longitude + 1) % RingSegments;
                int a = rings[ring][longitude];
                int b = rings[ring][next];
                int c = rings[ring + 1][longitude];
                int d = rings[ring + 1][next];
                Triangles.Add((a, b, c));
                Triangles.Add((b, d, c));
            }
        }

        private int AddPoint(Vector3D point)
        {
            Vector3D transformed = Transform(point);
            if (!transformed.IsFinite)
            {
                throw new InvalidOperationException("Fixture transform produced a non-finite point.");
            }

            Points.Add(transformed);
            return Points.Count - 1;
        }

        public Vector3D Transform(Vector3D point) =>
            _transform.TransformPoint(point * _scale);
    }
}

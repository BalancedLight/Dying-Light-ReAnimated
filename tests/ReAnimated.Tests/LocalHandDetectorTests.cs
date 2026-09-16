using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class LocalHandDetectorTests
{
    private static readonly string[] ExpectedDigits = ["index", "little", "middle", "ring", "thumb"];
    internal static SourceGeometryVolume Hand(bool fused = false, bool palmOnly = false, double scale = 1, bool bent = false) =>
        SourceGeometryVolume.Build(Geometry(fused, palmOnly, scale, bent));

    internal static SourceGeometryAnalysis Geometry(bool fused = false, bool palmOnly = false, double scale = 1, bool bent = false)
    {
        var boxes = new List<(Vector3D Min, Vector3D Max)> { (new(-.042, -.014, -.008), new(.042, .014, .085)) };
        if (!palmOnly)
        {
            if (fused) boxes.Add((new(-.04, -.009, .078), new(.04, .009, .16)));
            else
            {
                for (int i = 0; i < 4; i++)
                {
                    double x = -.03 + i * .02;
                    double length = new[] { .155, .172, .162, .142 }[i];
                    boxes.Add((new(x - .007, -.009, .077), new(x + .007, .009, bent && i == 1 ? .138 : length)));
                    if (bent && i == 1) boxes.Add((new(x - .007, -.05, .126), new(x + .007, .006, .14)));
                }
            }
            boxes.Add((new(-.093, -.012, .027), new(-.035, .012, .045)));
        }
        var components = boxes.Select((box, i) => Box("hand-shell-" + i, box.Min * scale, box.Max * scale)).ToImmutableArray();
        return new(new string('b', 64), components);
    }

    private static SourceGeometryComponentAnalysis Box(string id, Vector3D minimum, Vector3D maximum)
    {
        ImmutableArray<Vector3D> points = [new(minimum.X, minimum.Y, minimum.Z), new(maximum.X, minimum.Y, minimum.Z),
            new(maximum.X, maximum.Y, minimum.Z), new(minimum.X, maximum.Y, minimum.Z),
            new(minimum.X, minimum.Y, maximum.Z), new(maximum.X, minimum.Y, maximum.Z),
            new(maximum.X, maximum.Y, maximum.Z), new(minimum.X, maximum.Y, maximum.Z)];
        (int A, int B, int C)[] triangles = [(0,2,1),(0,3,2),(4,5,6),(4,6,7),(0,1,5),(0,5,4),(3,7,6),(3,6,2),(0,4,7),(0,7,3),(1,2,6),(1,6,5)];
        return new(new(id, points), triangles.Select((t, i) => new SourceGeometryAnalysisTriangle(new(i, 0), t.A, t.B, t.C)).ToImmutableArray(),
            SourceMeshTopology.Build(points.Length, triangles));
    }

    internal static SourceVolumeGrid Grid(SourceGeometryVolume volume, int resolution = 64) => SourceVolumeGrid.Build(volume, new() { LongestAxisCells = resolution });

    [Fact]
    public void SeparatedHandProducesFiveInteriorBranchesAndLocalPalmFrame()
    {
        var volume = Hand(); var grid = Grid(volume);
        var result = LocalHandDetector.Detect(grid, new());
        Assert.True(result.Fingers.Length == 5, string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)) + $" ({result.Fingers.Length} branches)");
        Assert.NotNull(result.PalmFrame);
        Assert.Equal(ExpectedDigits, result.Fingers.Select(f => f.Digit).Order(StringComparer.Ordinal));
        Assert.Equal(1, result.PalmFrame.Value.LinearDeterminant, 8);
        Assert.All(result.Fingers, f =>
        {
            Assert.Equal(4, f.Joints.Length);
            Assert.True(f.BranchLength > grid.CellSize * 5);
            Assert.True(f.RollAmbiguous); // Straight boxes do not supply reliable knuckle/roll geometry.
            var palmNormal = new Vector3D(result.PalmFrame.Value.M12, result.PalmFrame.Value.M22, result.PalmFrame.Value.M32);
            Assert.InRange(Math.Abs(Vector3D.Dot(f.CurlPlaneNormal, palmNormal)), 0, 1e-8);
            Assert.All(f.Joints, j => Assert.Equal(SourceVolumeLocation.Interior, volume.Sample(j.Position).Location));
        });
        Assert.True(result.RequiresReview);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void FusedDigitsAndPlainPalmDoNotBecomeACompleteFiveFingerRig(bool fused, bool palmOnly)
    {
        var result = LocalHandDetector.Detect(Grid(Hand(fused, palmOnly)), new());
        Assert.Equal(AnatomicalDetectionStatus.NeedsAssistance, result.Status);
        Assert.NotEqual(5, result.Fingers.Length);
        Assert.Contains(result.Diagnostics, d => d.Code is "hand_branch_count" or "hand_digit_count_mismatch" or "hand_digit_names_ambiguous");
    }

    [Fact]
    public void LocalScaleAndBentDigitHaveSeparateGeometryEvidence()
    {
        var small = LocalHandDetector.Detect(Grid(Hand(scale: .5)), new());
        var large = LocalHandDetector.Detect(Grid(Hand(scale: 2)), new());
        Assert.Equal(small.Fingers.Length, large.Fingers.Length);
        Assert.Equal(small.ResolutionMeters * 4, large.ResolutionMeters, 10);
        Assert.Equal(small.Fingers.Select(f => f.BranchLength * 4), large.Fingers.Select(f => f.BranchLength));
        var bent = LocalHandDetector.Detect(Grid(Hand(bent: true)), new());
        Assert.NotEmpty(bent.Fingers);
        Assert.Contains(bent.Fingers, f => f.Joints.Any(j => j.Method == AnatomicalPlacementMethod.PathBend));
    }

    [Fact]
    public void LocksAreRetainedAndConfigurationParticipatesInIdentity()
    {
        var grid = Grid(Hand());
        var guide = new AnatomicalGuide("finger.left.index.2", new(-.03, 0, .12));
        var options = new LocalHandDetectionOptions { Guides = [guide] };
        var result = LocalHandDetector.Detect(grid, options);
        Assert.Equal(guide.Position, Assert.Single(result.PreservedGuides).Position);
        Assert.True(result.PreservedGuides[0].Locked);
        Assert.NotEqual(result.InputFingerprint, LocalHandDetector.Detect(grid, options with { ExpectedDigits = 4 }).InputFingerprint);
        Assert.Throws<OperationCanceledException>(() => LocalHandDetector.Detect(grid, options, new(true)));
        Assert.Throws<ArgumentException>(() => LocalHandDetector.Detect(grid, options with { Forward = Vector3D.Zero }));
    }
}

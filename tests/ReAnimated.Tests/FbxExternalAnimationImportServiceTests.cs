using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;

namespace ReAnimated.Tests;

public sealed class FbxExternalAnimationImportServiceTests
{
    private static readonly long FrameTick =
        FbxBinaryDocument.TicksPerSecond / 30;

    [Fact]
    public void ScanEnumeratesEveryStackAndExplainsLayeredBakeRequirement()
    {
        FbxExternalAnimationScanResult scan =
            FbxExternalAnimationImportService.Scan(
                CreateDocument(),
                Options());

        Assert.Equal(3, scan.Stacks.Length);
        FbxExternalAnimationStackDescriptor combined = scan.Stacks[0];
        Assert.Equal(40, combined.StackObjectId);
        Assert.Equal(
            AnimationSourceRoles.Body | AnimationSourceRoles.Facial,
            combined.Roles);
        Assert.True(combined.CanImport);
        Assert.Equal(new FrameRate(30, 1), combined.FrameRate);
        Assert.Equal(2, combined.FrameCount);
        Assert.Equal(
            FbxFacialSourceValueUnit.Percent,
            combined.FacialSourceValueUnit);
        Assert.Matches("^[0-9a-f]{64}$", combined.SourceRigSignature);
        Assert.Matches("^[0-9a-f]{64}$", combined.StackFingerprint);
        Assert.Contains(
            combined.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "combined_body_facial_stack");

        FbxExternalAnimationStackDescriptor facial = scan.Stacks[1];
        Assert.Equal(AnimationSourceRoles.Facial, facial.Roles);
        Assert.True(facial.CanImport);
        Assert.Null(facial.SourceRigSignature);

        FbxExternalAnimationStackDescriptor layered = scan.Stacks[2];
        Assert.True(layered.RequiresLayerBake);
        Assert.False(layered.CanImport);
        Assert.Contains(
            layered.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "layered_stack_requires_bake" &&
                diagnostic.Message.Contains(
                    "bake or flatten",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImportCheckedStacksMergesBodyAndFacialCurvesInSourceOrder()
    {
        ImmutableArray<FbxExternalAnimationImportResult> imported =
            FbxExternalAnimationImportService.ImportSelected(
                CreateDocument(),
                [41, 40],
                Options());

        Assert.Equal(2, imported.Length);
        Assert.Equal(40, imported[0].Stack.StackObjectId);
        Assert.Equal(41, imported[1].Stack.StackObjectId);
        FbxExternalAnimationImportResult combined = imported[0];
        Assert.NotNull(combined.SourceRig);
        Assert.NotNull(combined.Body);
        Assert.NotNull(combined.Facial);
        Assert.Single(combined.Clip.TransformTracks);
        Assert.Single(combined.Clip.ScalarTracks);
        Assert.Equal(
            2.0,
            combined.Clip
                .SamplePose(combined.SourceRig!, 1.0 / 30.0)
                .LocalTransforms[0]
                .Translation.X,
            10);
        Assert.Equal(
            1.0,
            combined.Clip.SampleScalars(1.0 / 30.0)["smile"],
            10);
        Assert.Equal(
            0.5,
            imported[1].Clip.SampleScalars(1.0 / 30.0)["smile"],
            10);
    }

    [Fact]
    public void ExplicitNormalizedDeformPercentUnitIsNeverRangeInferred()
    {
        FbxExternalAnimationImportOptions options = Options() with
        {
            FacialSourceValueUnit =
                FbxFacialSourceValueUnit.Normalized,
        };

        FbxExternalAnimationImportResult imported = Assert.Single(
            FbxExternalAnimationImportService.ImportSelected(
                CreateDocument(),
                [40],
                options));

        Assert.Equal(
            FbxFacialSourceValueUnit.Normalized,
            imported.FacialSourceValueUnit);
        Assert.Equal(
            100.0,
            imported.Clip.SampleScalars(1.0 / 30.0)["smile"],
            10);
    }

    [Fact]
    public void ImportRejectsLayeredCheckedStackWithBakeGuidance()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxExternalAnimationImportService.ImportSelected(
                CreateDocument(),
                [42],
                Options()));

        Assert.Contains(
            "bake or flatten",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExternalWorkflowProjectsAffineRigTransformsAndReportsWarning()
    {
        FbxBinaryDocument document = CreateAffineDocument(
            singularBind: false);
        FbxExternalAnimationImportOptions strict = Options() with
        {
            ProjectAffineShearToTrs = false,
        };

        FbxExternalAnimationStackDescriptor rejected = Assert.Single(
            FbxExternalAnimationImportService.Scan(
                document,
                strict).Stacks);
        Assert.False(rejected.CanImport);
        Assert.Contains(
            rejected.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "body_import_failed" &&
                diagnostic.Severity ==
                    FbxExternalAnimationDiagnosticSeverity.Error);

        FbxExternalAnimationStackDescriptor projected = Assert.Single(
            FbxExternalAnimationImportService.Scan(
                document,
                Options()).Stacks);
        Assert.True(projected.CanImport);
        Assert.Equal(AnimationSourceRoles.Body, projected.Roles);
        Assert.Contains(
            projected.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "body_affine_trs_projection" &&
                diagnostic.Severity ==
                    FbxExternalAnimationDiagnosticSeverity.Warning);

        FbxExternalAnimationImportResult imported = Assert.Single(
            FbxExternalAnimationImportService.ImportSelected(
                document,
                [projected.StackObjectId],
                Options()));
        Assert.NotNull(imported.SourceRig);
        Assert.Single(imported.Clip.TransformTracks);
    }

    [Fact]
    public void ExternalWorkflowStillRejectsSingularBindTransforms()
    {
        FbxExternalAnimationStackDescriptor row = Assert.Single(
            FbxExternalAnimationImportService.Scan(
                CreateAffineDocument(singularBind: true),
                Options()).Stacks);

        Assert.False(row.CanImport);
        Assert.Contains(
            row.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "body_import_failed" &&
                diagnostic.Severity ==
                    FbxExternalAnimationDiagnosticSeverity.Error &&
                diagnostic.Message.Contains(
                    "non-singular",
                    StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            row.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "body_affine_trs_projection");
    }

    private static FbxExternalAnimationImportOptions Options() =>
        new()
        {
            Body = new FbxCoreAnimationImportOptions
            {
                RigId = "fbx:generic-source",
                RigDisplayName = "Generic source",
                ConvertUnitsToMeters = false,
            },
        };

    private static FbxBinaryDocument CreateDocument() =>
        Document(
            [
                Model(1, "root", "LimbNode"),
                Channel(10, "smile", 0.0),
                Stack(40, "Combined", 0, FrameTick),
                Stack(41, "FacialOnly", 0, FrameTick),
                Stack(42, "NeedsBake", 0, FrameTick),
                Layer(100, "CombinedLayer"),
                Layer(101, "FacialLayer"),
                Layer(102, "FirstLayer"),
                Layer(103, "SecondLayer"),
                CurveNode(20, "body"),
                CurveNode(21, "combinedFace"),
                CurveNode(22, "facialOnly"),
                Curve(30, [0, FrameTick], [0.0, 2.0]),
                Curve(31, [0, FrameTick], [0.0, 100.0]),
                Curve(32, [0, FrameTick], [0.0, 50.0]),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 101, 41),
                Connection("OO", 102, 42),
                Connection("OO", 103, 42),
                Connection("OO", 20, 100),
                Connection("OO", 21, 100),
                Connection("OO", 22, 101),
                Connection("OP", 20, 1, "Lcl Translation"),
                Connection("OP", 30, 20, "d|X"),
                Connection("OP", 21, 10, "DeformPercent"),
                Connection("OP", 31, 21, "d|DeformPercent"),
                Connection("OP", 22, 10, "DeformPercent"),
                Connection("OP", 32, 22, "d|DeformPercent"),
            ],
            GlobalSettings(Property70("TimeMode", 6)));

    private static FbxBinaryDocument CreateAffineDocument(
        bool singularBind)
    {
        ImmutableArray<double> bind = singularBind
            ? ImmutableArray.Create(
                0.0, 0.0, 0.0, 0.0,
                0.0, 1.0, 0.0, 0.0,
                0.0, 0.0, 1.0, 0.0,
                0.0, 0.0, 0.0, 1.0)
            : ImmutableArray.Create(
                1.0, 0.0, 0.0, 0.0,
                0.25, 1.0, 0.0, 0.0,
                0.0, 0.0, 1.0, 0.0,
                0.0, 0.0, 0.0, 1.0);
        return Document(
            [
                Model(1, "root", "LimbNode"),
                BindPose(50, (1, bind)),
                Stack(40, "Affine", 0, FrameTick),
                Layer(100, "Base"),
                CurveNode(20, "body"),
                Curve(30, [0, FrameTick], [0.0, 1.0]),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 20, 100),
                Connection("OP", 20, 1, "Lcl Translation"),
                Connection("OP", 30, 20, "d|X"),
            ],
            GlobalSettings(Property70("TimeMode", 6)));
    }

    private static FbxNode Model(
        long objectId,
        string name,
        string subtype) =>
        Node(
            "Model",
            [objectId, $"Model::{name}", subtype],
            Node(
                "Properties70",
                [],
                Property70("Lcl Translation", 0.0, 0.0, 0.0),
                Property70("Lcl Rotation", 0.0, 0.0, 0.0),
                Property70("Lcl Scaling", 1.0, 1.0, 1.0)));

    private static FbxNode Channel(
        long objectId,
        string name,
        double defaultValue) =>
        Node(
            "Deformer",
            [objectId, $"SubDeformer::{name}", "BlendShapeChannel"],
            Node("DeformPercent", [defaultValue]));

    private static FbxNode Stack(
        long objectId,
        string name,
        long start,
        long stop) =>
        Node(
            "AnimationStack",
            [objectId, $"AnimStack::{name}", string.Empty],
            Node(
                "Properties70",
                [],
                Property70("LocalStart", start),
                Property70("LocalStop", stop)));

    private static FbxNode Layer(long objectId, string name) =>
        Node(
            "AnimationLayer",
            [objectId, $"AnimLayer::{name}", string.Empty]);

    private static FbxNode CurveNode(long objectId, string name) =>
        Node(
            "AnimationCurveNode",
            [objectId, $"AnimationCurveNode::{name}", string.Empty]);

    private static FbxNode Curve(
        long objectId,
        long[] keyTimes,
        double[] keyValues) =>
        Node(
            "AnimationCurve",
            [objectId, $"AnimationCurve::{objectId}", string.Empty],
            Node("KeyTime", [keyTimes.ToImmutableArray()]),
            Node("KeyValueFloat", [keyValues.ToImmutableArray()]),
            Node(
                "KeyAttrFlags",
                [ImmutableArray.Create(0x00006104)]),
            Node(
                "KeyAttrDataFloat",
                [ImmutableArray.Create(0.0f, 0.0f, 0.0f, 0.0f)]),
            Node(
                "KeyAttrRefCount",
                [ImmutableArray.Create(keyTimes.Length)]));

    private static FbxNode BindPose(
        long objectId,
        params (long ObjectId, ImmutableArray<double> Matrix)[] rows)
    {
        FbxNode[] children =
        [
            Node("Type", ["BindPose"]),
            .. rows.Select(static row =>
                Node(
                    "PoseNode",
                    [],
                    Node("Node", [row.ObjectId]),
                    Node("Matrix", [row.Matrix]))),
        ];
        return Node(
            "Pose",
            [objectId, "Pose::BindPose", "BindPose"],
            children);
    }

    private static FbxNode GlobalSettings(params FbxNode[] properties) =>
        Node(
            "GlobalSettings",
            [],
            Node("Properties70", [], properties));

    private static FbxNode Property70(
        string name,
        params object[] values) =>
        Node("P", [name, name, string.Empty, "A", .. values]);

    private static FbxNode Connection(
        string kind,
        long childId,
        long parentId,
        params object[] metadata) =>
        Node("C", [kind, childId, parentId, .. metadata]);

    private static FbxBinaryDocument Document(
        FbxNode[] objects,
        FbxNode[] connections,
        FbxNode globalSettings) =>
        new(
            7400,
            [
                globalSettings,
                Node("Objects", [], objects),
                Node("Connections", [], connections),
            ]);

    private static FbxNode Node(
        string name,
        object[] properties,
        params FbxNode[] children) =>
        new(
            name,
            properties.Select(Property).ToImmutableArray(),
            children.ToImmutableArray(),
            0,
            0);

    private static FbxProperty Property(object value) =>
        new(
            value switch
            {
                long => 'L',
                int => 'I',
                float => 'F',
                double => 'D',
                string => 'S',
                ImmutableArray<int> => 'i',
                ImmutableArray<float> => 'f',
                ImmutableArray<long> => 'l',
                ImmutableArray<double> => 'd',
                _ => 'R',
            },
            value);
}

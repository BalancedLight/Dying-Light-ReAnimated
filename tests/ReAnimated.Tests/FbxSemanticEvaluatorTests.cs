using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class FbxSemanticEvaluatorTests
{
    [Theory]
    [InlineData(
        0,
        0.626892478275402, -0.762327387911748, -0.160819073251201,
        0.584586693378039, 0.596699964203826, -0.549734072661030,
        0.515038074910054, 0.250611464938863, 0.819713166317427)]
    [InlineData(
        1,
        0.626892478275402, -0.709625595108693, -0.321616752437366,
        0.681998360062498, 0.699397023149583, -0.213827128497681,
        0.376675002560280, -0.085295479224486, 0.922410225263184)]
    [InlineData(
        2,
        0.626892478275402, -0.681998360062498, -0.376675002560280,
        0.408460475191554, 0.699397023149583, -0.586518409102213,
        0.663449968639696, 0.213827128497681, 0.717016107371670)]
    [InlineData(
        3,
        0.729589537221159, -0.652198275286758, -0.205758394459115,
        0.474457580753049, 0.699397023149583, -0.534540745009644,
        0.492533360538531, 0.292371704722737, 0.819713166317427)]
    [InlineData(
        4,
        0.524195419329645, -0.694715806003029, -0.492533360538531,
        0.652198275286758, 0.699397023149583, -0.292371704722737,
        0.547591610661445, -0.167969499907163, 0.819713166317427)]
    [InlineData(
        5,
        0.626892478275402, -0.584586693378039, -0.515038074910054,
        0.542069162661768, 0.802094082095340, -0.250611464938863,
        0.559613119550368, -0.122079815665669, 0.819713166317427)]
    public void EvaluatesAllFbxEulerOrdersAgainstPythonOracle(
        int rotationOrder,
        double m11,
        double m12,
        double m13,
        double m21,
        double m22,
        double m23,
        double m31,
        double m32,
        double m33)
    {
        FbxBinaryDocument document = Document(
            [
                Model(
                    1,
                    "root",
                    "LimbNode",
                    Property70("Lcl Rotation", 17.0, -31.0, 43.0),
                    Property70("RotationOrder", rotationOrder)),
                Layer(100, "Base"),
            ],
            []);
        FbxModelObject model = FbxSemanticScene.Parse(document).Models[1];

        TransformMatrix actual = FbxTransformEvaluator.EvaluateModelLocal(model);

        AssertMatrixLinearNear(
            new TransformMatrix(
                m11, m12, m13, 0.0,
                m21, m22, m23, 0.0,
                m31, m32, m33, 0.0,
                0.0, 0.0, 0.0, 1.0),
            actual,
            1e-12);
    }

    [Fact]
    public void AppliesRotationPivotOffsetAndPrePostRotationsInFbxOrder()
    {
        FbxModelObject model = ParseSingleModel(
            Property70("Lcl Translation", 1.0, 2.0, 3.0),
            Property70("Lcl Rotation", 0.0, 0.0, 90.0),
            Property70("PreRotation", 0.0, 0.0, 30.0),
            Property70("PostRotation", 0.0, 0.0, 10.0),
            Property70("RotationOffset", 0.0, 5.0, 0.0),
            Property70("RotationPivot", 10.0, 0.0, 0.0));

        TransformMatrix actual = FbxTransformEvaluator.EvaluateModelLocal(model);
        double angle = 110.0 * Math.PI / 180.0;

        AssertVectorNear(
            new Vector3D(Math.Cos(angle), Math.Sin(angle), 0.0),
            actual.TransformDirection(Vector3D.UnitX));
        AssertVectorNear(
            new Vector3D(
                11.0 - (10.0 * Math.Cos(angle)),
                7.0 - (10.0 * Math.Sin(angle)),
                3.0),
            actual.Translation);
    }

    [Fact]
    public void AppliesScalingPivotAndOffsetInFbxOrder()
    {
        FbxModelObject model = ParseSingleModel(
            Property70("Lcl Translation", 1.0, 2.0, 3.0),
            Property70("Lcl Scaling", 2.0, 3.0, 4.0),
            Property70("ScalingOffset", 0.0, 1.0, 0.0),
            Property70("ScalingPivot", 0.0, 2.0, 0.0));

        TransformMatrix actual = FbxTransformEvaluator.EvaluateModelLocal(model);

        AssertVectorNear(new Vector3D(1.0, -1.0, 3.0), actual.Translation);
        AssertVectorNear(new Vector3D(1.0, 2.0, 3.0), actual.TransformPoint(Vector3D.UnitY));
        AssertVectorNear(new Vector3D(2.0, 0.0, 0.0), actual.TransformDirection(Vector3D.UnitX));
    }

    [Fact]
    public void CollapsesIntermediateNullAndConvertsCentimetersToMeters()
    {
        FbxBinaryDocument document = Document(
            [
                Model(
                    1,
                    "root",
                    "LimbNode",
                    Property70("Lcl Translation", 0.0, 100.0, 0.0)),
                Model(
                    2,
                    "axis_helper",
                    "Null",
                    Property70("Lcl Rotation", 0.0, 0.0, 90.0)),
                Model(
                    3,
                    "child",
                    "LimbNode",
                    Property70("Lcl Translation", 50.0, 0.0, 0.0)),
                Stack(40, "Take", 0, 0),
                Layer(100, "Base"),
            ],
            [
                Connection("OO", 2, 1),
                Connection("OO", 3, 2),
                Connection("OO", 100, 40),
            ],
            GlobalSettings(
                Property70("UnitScaleFactor", 1.0),
                Property70("CoordAxis", 0),
                Property70("CoordAxisSign", 1),
                Property70("UpAxis", 1),
                Property70("UpAxisSign", 1),
                Property70("FrontAxis", 2),
                Property70("FrontAxisSign", 1),
                Property70("TimeMode", 11)));

        FbxCoreAnimationImportResult result = FbxCoreAnimationAdapter.Import(document);

        Assert.Equal(2, result.Rig.BoneCount);
        Assert.Equal(-1, result.Rig.Bones[0].ParentIndex);
        Assert.Equal(0, result.Rig.Bones[1].ParentIndex);
        AssertVectorNear(
            new Vector3D(0.0, 1.0, 0.0),
            result.Rig.Bones[0].LocalBindPose.Translation);
        AssertVectorNear(
            new Vector3D(0.0, 0.5, 0.0),
            result.Rig.Bones[1].LocalBindPose.Translation);
        AssertVectorNear(
            Vector3D.UnitY,
            result.Rig.Bones[1].LocalBindPose.Rotation.Rotate(Vector3D.UnitX));
        Assert.Equal(0.01, result.MetersPerUnit, 12);
    }

    [Fact]
    public void ConvertsSignedGlobalAxesAtTheCoreMatrixBoundary()
    {
        FbxBinaryDocument document = Document(
            [
                Model(
                    1,
                    "root",
                    "LimbNode",
                    Property70("Lcl Translation", 0.0, 100.0, 0.0)),
                Stack(40, "Take", 0, 0),
                Layer(100, "Base"),
            ],
            [Connection("OO", 100, 40)],
            GlobalSettings(
                Property70("UnitScaleFactor", 1.0),
                Property70("CoordAxis", 0),
                Property70("CoordAxisSign", 1),
                Property70("UpAxis", 2),
                Property70("UpAxisSign", 1),
                Property70("FrontAxis", 1),
                Property70("FrontAxisSign", -1),
                Property70("TimeMode", 11)));

        FbxCoreAnimationImportResult result = FbxCoreAnimationAdapter.Import(document);

        TransformMatrix expectedBasis = new(
            1.0, 0.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, -1.0, 0.0, 0.0,
            0.0, 0.0, 0.0, 1.0);
        Assert.True(result.SceneToCoreBasis.NearlyEquals(expectedBasis, 1e-12));
        AssertVectorNear(
            new Vector3D(0.0, 0.0, -1.0),
            result.Rig.Bones[0].LocalBindPose.Translation);
    }

    [Fact]
    public void SamplesStackCurvesAtDeclaredRateIntoCoreClip()
    {
        long stop = FbxBinaryDocument.TicksPerSecond / 12;
        FbxBinaryDocument document = AnimatedRootDocument(
            stop,
            timeMode: 11,
            [
                CurveBinding(
                    20,
                    30,
                    "Lcl Translation",
                    'X',
                    [0, stop],
                    [0.0, 100.0]),
                CurveBinding(
                    21,
                    31,
                    "Lcl Rotation",
                    'Z',
                    [0, stop],
                    [0.0, 90.0]),
                CurveBinding(
                    22,
                    32,
                    "Lcl Scaling",
                    'X',
                    [0, stop],
                    [1.0, 2.0]),
            ]);

        FbxCoreAnimationImportResult result = FbxCoreAnimationAdapter.Import(document);
        SkeletonPose middle = result.Clip.SamplePose(
            result.Rig,
            1.0 / 24.0);
        TransformTRS transform = middle.LocalTransforms[0];

        Assert.Equal(new FrameRate(24, 1), result.DeclaredTimebase.FrameRate);
        Assert.Equal(new FrameRate(24, 1), result.Clip.FrameRate);
        Assert.Equal(3, result.Clip.FrameCount);
        Assert.Equal(3, result.SampleTicks.Length);
        AssertVectorNear(new Vector3D(0.5, 0.0, 0.0), transform.Translation);
        AssertVectorNear(new Vector3D(1.5, 1.0, 1.0), transform.Scale);
        double squareRootHalf = Math.Sqrt(0.5);
        AssertVectorNear(
            new Vector3D(squareRootHalf, squareRootHalf, 0.0),
            transform.Rotation.Rotate(Vector3D.UnitX));
    }

    [Fact]
    public void ResolvesDeclaredFractionalAndCustomTimeModesExactly()
    {
        FbxDeclaredTimebase fractional = ParseEmptyScene(
                GlobalSettings(Property70("TimeMode", 13)))
            .ResolveDeclaredTimebase([]);
        FbxDeclaredTimebase custom = ParseEmptyScene(
                GlobalSettings(
                    Property70("TimeMode", 14),
                    Property70("CustomFrameRate", 23.976)))
            .ResolveDeclaredTimebase([]);

        Assert.Equal(new FrameRate(24_000, 1_001), fractional.FrameRate);
        Assert.Equal(FbxTimebaseConfidence.Declared, fractional.Confidence);
        Assert.Equal(23.976, custom.FramesPerSecond, 9);
        Assert.Equal(FbxTimebaseSource.GlobalSettings, custom.Source);
    }

    [Fact]
    public void InfersTimebaseOnlyFromWithinCurveKeySpacing()
    {
        long step = FbxBinaryDocument.TicksPerSecond / 30;
        FbxBinaryDocument document = AnimatedRootDocument(
            step * 2,
            timeMode: 99,
            [
                CurveBinding(
                    20,
                    30,
                    "Lcl Translation",
                    'X',
                    [0, step, step * 2],
                    [0.0, 1.0, 2.0]),
            ]);
        FbxSemanticScene scene = FbxSemanticScene.Parse(document);
        ImmutableArray<FbxAnimationCurveBinding> bindings =
            scene.ReadAnimationBindings(scene.SelectAnimationStack(null));

        FbxDeclaredTimebase timebase = scene.ResolveDeclaredTimebase(bindings);

        Assert.Equal(new FrameRate(30, 1), timebase.FrameRate);
        Assert.Equal(FbxTimebaseSource.AnimationCurveKeySpacing, timebase.Source);
        Assert.Equal(FbxTimebaseConfidence.InferredLow, timebase.Confidence);
    }

    [Fact]
    public void TakesLocalTimeOverridesAnimationStackRange()
    {
        FbxBinaryDocument document = Document(
            [
                Model(1, "root", "LimbNode"),
                Stack(40, "Take", 0, 1),
                Layer(100, "Base"),
                Node(
                    "AnimationCurveNode",
                    [20L, "AnimationCurveNode::Move", string.Empty]),
                Curve(30, [100, 200], [1.0, 2.0]),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 20, 100),
                Connection("OP", 20, 1, "Lcl Translation"),
                Connection("OP", 30, 20, "d|X"),
            ],
            takes: Node(
                "Takes",
                [],
                Node(
                    "Take",
                    ["Take"],
                    Node("LocalTime", [100L, 200L]))));

        FbxAnimationStackInfo stack =
            FbxSemanticScene.Parse(document).SelectAnimationStack(null);

        Assert.Equal(100, stack.StartTick);
        Assert.Equal(200, stack.StopTick);
        FbxCoreAnimationImportResult imported =
            FbxCoreAnimationAdapter.Import(
                document,
                new FbxCoreAnimationImportOptions
                {
                    ConvertUnitsToMeters = false,
                });
        Assert.True(
            ImmutableArray.Create(100L, 200L)
                .AsSpan()
                .SequenceEqual(imported.SampleTicks.AsSpan()));
        Assert.Equal(
            1.0,
            imported.Clip.TransformTracks[0].Keyframes[0].Value.Translation.X);
        Assert.Equal(
            2.0,
            imported.Clip.TransformTracks[0].Keyframes[1].Value.Translation.X);
    }

    [Fact]
    public void RequiresExplicitSelectionWhenMultipleStacksExist()
    {
        FbxSemanticScene scene = FbxSemanticScene.Parse(
            Document(
                [
                    Model(1, "root", "LimbNode"),
                    Stack(40, "First", 0, 0),
                    Stack(41, "Second", 0, 0),
                    Layer(100, "FirstLayer"),
                    Layer(101, "SecondLayer"),
                    Node(
                        "AnimationCurveNode",
                        [20L, "AnimationCurveNode::First", string.Empty]),
                    Node(
                        "AnimationCurveNode",
                        [21L, "AnimationCurveNode::Second", string.Empty]),
                    Curve(30, [0], [1.0]),
                    Curve(31, [0], [2.0]),
                ],
                [
                    Connection("OO", 100, 40),
                    Connection("OO", 101, 41),
                    Connection("OO", 20, 100),
                    Connection("OO", 21, 101),
                    Connection("OP", 20, 1, "Lcl Translation"),
                    Connection("OP", 21, 1, "Lcl Translation"),
                    Connection("OP", 30, 20, "d|X"),
                    Connection("OP", 31, 21, "d|X"),
                ]));

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => scene.SelectAnimationStack(null));

        Assert.Contains("select one explicitly", error.Message, StringComparison.Ordinal);
        FbxAnimationStackInfo first = scene.SelectAnimationStack("First");
        FbxAnimationStackInfo second = scene.SelectAnimationStack("Second");
        Assert.Equal(
            1.0,
            Assert.Single(scene.ReadAnimationBindings(first))
                .Curve.KeyValues[0]);
        Assert.Equal(
            2.0,
            Assert.Single(scene.ReadAnimationBindings(second))
                .Curve.KeyValues[0]);
    }

    [Fact]
    public void ImportPrefersTheUniqueChangingSkeletalStackOverStaticPeer()
    {
        FbxBinaryDocument document = Document(
            [
                Model(1, "root", "LimbNode"),
                Stack(40, "Static", 0, 10),
                Stack(41, "Changing", 0, 10),
                Layer(100, "StaticLayer"),
                Layer(101, "ChangingLayer"),
                Node(
                    "AnimationCurveNode",
                    [20L, "AnimationCurveNode::Static", string.Empty]),
                Node(
                    "AnimationCurveNode",
                    [21L, "AnimationCurveNode::Changing", string.Empty]),
                Curve(30, [0, 10], [1.0, 1.0]),
                Curve(31, [0, 10], [8.0, 9.0]),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 101, 41),
                Connection("OO", 20, 100),
                Connection("OO", 21, 101),
                Connection("OP", 20, 1, "Lcl Translation"),
                Connection("OP", 21, 1, "Lcl Translation"),
                Connection("OP", 30, 20, "d|X"),
                Connection("OP", 31, 21, "d|X"),
            ]);
        FbxSemanticScene scene = FbxSemanticScene.Parse(document);

        Assert.Throws<InvalidDataException>(
            () => scene.SelectAnimationStack(null));
        Assert.Equal(
            "Changing",
            scene.SelectAnimationStackForImport(null).Name);
        FbxAnimationStackActivity staticActivity = Assert.Single(
            scene.AnalyzeAnimationStacks(),
            static activity =>
                activity.Stack.Name == "Static");
        Assert.True(staticActivity.Usable);
        Assert.Equal(1, staticActivity.SkeletalBindingCount);
        Assert.Equal(0, staticActivity.ChangingSkeletalBindingCount);
        FbxAnimationStackActivity changingActivity = Assert.Single(
            scene.AnalyzeAnimationStacks(),
            static activity =>
                activity.Stack.Name == "Changing");
        Assert.True(changingActivity.Usable);
        Assert.Equal(1, changingActivity.SkeletalBindingCount);
        Assert.Equal(1, changingActivity.ChangingSkeletalBindingCount);

        FbxCoreAnimationImportResult imported =
            FbxCoreAnimationAdapter.Import(
                document,
                new FbxCoreAnimationImportOptions
                {
                    ConvertUnitsToMeters = false,
                });
        Assert.Equal("Changing", imported.AnimationStack.Name);
        Assert.Equal(
            8.0,
            imported.Clip
                .SamplePose(imported.Rig, 0.0)
                .LocalTransforms[0]
                .Translation.X);
        Assert.Equal(
            9.0,
            imported.Clip
                .SamplePose(imported.Rig, 1.0 / 30.0)
                .LocalTransforms[0]
                .Translation.X);
    }

    [Fact]
    public void MalformedCurveOnlyMakesItsOwningStackUnusable()
    {
        FbxBinaryDocument document = Document(
            [
                Model(1, "root", "LimbNode"),
                Stack(40, "Usable", 0, 10),
                Stack(41, "Malformed", 0, 10),
                Layer(100, "UsableLayer"),
                Layer(101, "MalformedLayer"),
                Node(
                    "AnimationCurveNode",
                    [20L, "AnimationCurveNode::Usable", string.Empty]),
                Node(
                    "AnimationCurveNode",
                    [21L, "AnimationCurveNode::Malformed", string.Empty]),
                Curve(30, [0, 10], [1.0, 2.0]),
                Curve(31, [0, 10], [8.0]),
            ],
            [
                Connection("OO", 100, 40),
                Connection("OO", 101, 41),
                Connection("OO", 20, 100),
                Connection("OO", 21, 101),
                Connection("OP", 20, 1, "Lcl Translation"),
                Connection("OP", 21, 1, "Lcl Translation"),
                Connection("OP", 30, 20, "d|X"),
                Connection("OP", 31, 21, "d|X"),
            ]);

        FbxSemanticScene scene = FbxSemanticScene.Parse(document);
        FbxAnimationStackActivity malformed = Assert.Single(
            scene.AnalyzeAnimationStacks(),
            static activity =>
                activity.Stack.Name == "Malformed");
        Assert.False(malformed.Usable);
        Assert.Contains(
            "equal non-empty",
            malformed.UnavailableReason,
            StringComparison.Ordinal);
        Assert.Equal(
            "Usable",
            scene.SelectAnimationStackForImport(null).Name);

        FbxCoreAnimationImportResult imported =
            FbxCoreAnimationAdapter.Import(
                document,
                new FbxCoreAnimationImportOptions
                {
                    ConvertUnitsToMeters = false,
                });
        Assert.Equal("Usable", imported.AnimationStack.Name);

        InvalidDataException explicitError =
            Assert.Throws<InvalidDataException>(
                () => FbxCoreAnimationAdapter.Import(
                    document,
                    new FbxCoreAnimationImportOptions
                    {
                        AnimationStackName = "Malformed",
                        ConvertUnitsToMeters = false,
                    }));
        Assert.Contains(
            "animation stack 'Malformed'",
            explicitError.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "equal non-empty",
            explicitError.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImportPrefersAuthoritativeBindPoseGlobalsOverModelFallback()
    {
        FbxBinaryDocument document = Document(
            [
                Model(
                    1,
                    "root",
                    "LimbNode",
                    Property70("Lcl Translation", 100.0, 0.0, 0.0),
                    Property70("Lcl Rotation", 0.0, 0.0, 0.0),
                    Property70("Lcl Scaling", 1.0, 1.0, 1.0)),
                Model(
                    2,
                    "child",
                    "LimbNode",
                    Property70("Lcl Translation", 2.0, 0.0, 0.0),
                    Property70("Lcl Rotation", 0.0, 0.0, 0.0),
                    Property70("Lcl Scaling", 1.0, 1.0, 1.0)),
                BindPose(
                    50,
                    (1, FbxRowVectorTranslationMatrix(10.0, 0.0, 0.0)),
                    (2, FbxRowVectorTranslationMatrix(13.0, 0.0, 0.0))),
                Stack(40, "Take", 0, 0),
                Layer(100, "Base"),
            ],
            [
                Connection("OO", 2, 1),
                Connection("OO", 100, 40),
            ]);

        FbxCoreAnimationImportResult imported =
            FbxCoreAnimationAdapter.Import(
                document,
                new FbxCoreAnimationImportOptions
                {
                    ConvertUnitsToMeters = false,
                });

        AssertVectorNear(
            new Vector3D(10.0, 0.0, 0.0),
            imported.Rig.Bones[0].LocalBindPose.Translation);
        AssertVectorNear(
            new Vector3D(3.0, 0.0, 0.0),
            imported.Rig.Bones[1].LocalBindPose.Translation);
        Assert.Equal(
            new Vector3D(100.0, 0.0, 0.0),
            imported.Clip.TransformTracks[0].Keyframes[0].Value.Translation);
    }

    [Fact]
    public void AutoSelectionIgnoresChangingPropertiesTheEvaluatorDoesNotApply()
    {
        FbxSemanticScene scene = FbxSemanticScene.Parse(
            Document(
                [
                    Model(1, "root", "LimbNode"),
                    Stack(40, "Transform", 0, 10),
                    Stack(41, "Visibility", 0, 10),
                    Layer(100, "TransformLayer"),
                    Layer(101, "VisibilityLayer"),
                    Node(
                        "AnimationCurveNode",
                        [20L, "AnimationCurveNode::Transform", string.Empty]),
                    Node(
                        "AnimationCurveNode",
                        [21L, "AnimationCurveNode::Visibility", string.Empty]),
                    Curve(30, [0, 10], [1.0, 1.0]),
                    Curve(31, [0, 10], [0.0, 1.0]),
                ],
                [
                    Connection("OO", 100, 40),
                    Connection("OO", 101, 41),
                    Connection("OO", 20, 100),
                    Connection("OO", 21, 101),
                    Connection("OP", 20, 1, "Lcl Translation"),
                    Connection("OP", 21, 1, "Visibility"),
                    Connection("OP", 30, 20, "d|X"),
                    Connection("OP", 31, 21, "d|X"),
                ]));

        Assert.Equal(
            "Transform",
            scene.SelectAnimationStackForImport(null).Name);
        FbxAnimationStackActivity visibility = Assert.Single(
            scene.AnalyzeAnimationStacks(),
            static activity => activity.Stack.Name == "Visibility");
        Assert.Equal(0, visibility.SkeletalBindingCount);
        Assert.Equal(0, visibility.ChangingSkeletalBindingCount);
    }

    [Fact]
    public void RejectsAggregateSampledTransformKeysBeforeTrackAllocation()
    {
        FbxBinaryDocument document = Document(
            [
                Model(1, "root", "LimbNode"),
                Model(2, "child", "LimbNode"),
                Stack(40, "Take", 0, 0),
                Layer(100, "Base"),
            ],
            [
                Connection("OO", 2, 1),
                Connection("OO", 100, 40),
            ]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxCoreAnimationAdapter.Import(
                document,
                new FbxCoreAnimationImportOptions
                {
                    ConvertUnitsToMeters = false,
                    MaximumSampledTransformKeys = 1,
                }));

        Assert.Contains(
            "2 sampled transform keys",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "2 bones x 1 frames",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HonorsCancellationDuringSemanticImport()
    {
        FbxBinaryDocument document = AnimatedRootDocument(
            FbxBinaryDocument.TicksPerSecond,
            timeMode: 30,
            [
                CurveBinding(
                    20,
                    30,
                    "Lcl Translation",
                    'X',
                    [0, FbxBinaryDocument.TicksPerSecond],
                    [0.0, 1.0]),
            ]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => FbxCoreAnimationAdapter.Import(
                document,
                options: null,
                cancellation.Token));
    }

    [Fact]
    public void RequiresAnimationStackToBeBakedToOneLayer()
    {
        FbxSemanticScene scene = FbxSemanticScene.Parse(
            Document(
                [
                    Model(1, "root", "LimbNode"),
                    Stack(40, "Take", 0, 0),
                    Layer(100, "First"),
                    Layer(101, "Second"),
                ],
                [
                    Connection("OO", 100, 40),
                    Connection("OO", 101, 40),
                ]));

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => scene.ReadAnimationBindings(scene.SelectAnimationStack(null)));

        Assert.Contains("bake or flatten", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDecreasingCurveTimesAndHierarchyCycles()
    {
        FbxBinaryDocument decreasingCurve = AnimatedRootDocument(
            10,
            timeMode: 30,
            [
                CurveBinding(
                    20,
                    30,
                    "Lcl Translation",
                    'X',
                    [10, 0],
                    [1.0, 0.0]),
            ]);
        FbxSemanticScene decreasingScene =
            FbxSemanticScene.Parse(decreasingCurve);
        FbxAnimationStackActivity decreasingActivity = Assert.Single(
            decreasingScene.AnalyzeAnimationStacks());
        Assert.False(decreasingActivity.Usable);
        Assert.Contains(
            "decreasing KeyTime",
            decreasingActivity.UnavailableReason,
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(
            () => FbxCoreAnimationAdapter.Import(decreasingCurve));

        FbxBinaryDocument cycle = Document(
            [
                Model(1, "root", "LimbNode"),
                Model(2, "child", "LimbNode"),
                Stack(40, "Take", 0, 0),
                Layer(100, "Base"),
            ],
            [
                Connection("OO", 1, 2),
                Connection("OO", 2, 1),
                Connection("OO", 100, 40),
            ]);
        InvalidDataException cycleError = Assert.Throws<InvalidDataException>(
            () => FbxCoreAnimationAdapter.Import(cycle));

        Assert.Contains("cycle", cycleError.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FbxModelObject ParseSingleModel(params FbxNode[] properties)
    {
        FbxBinaryDocument document = Document(
            [
                Model(1, "root", "LimbNode", properties),
                Layer(100, "Base"),
            ],
            []);
        return FbxSemanticScene.Parse(document).Models[1];
    }

    private static FbxSemanticScene ParseEmptyScene(FbxNode globalSettings) =>
        FbxSemanticScene.Parse(
            Document(
                [
                    Model(1, "root", "LimbNode"),
                    Layer(100, "Base"),
                ],
                [],
                globalSettings));

    private static FbxBinaryDocument AnimatedRootDocument(
        long stop,
        int timeMode,
        CurveBindingSpec[] curveBindings)
    {
        var objects = new List<FbxNode>
        {
            Model(1, "root", "LimbNode"),
            Stack(40, "Take", 0, stop),
            Layer(100, "Base"),
        };
        var connections = new List<FbxNode>
        {
            Connection("OO", 100, 40),
        };
        foreach (CurveBindingSpec binding in curveBindings)
        {
            objects.Add(
                Node(
                    "AnimationCurveNode",
                    [binding.CurveNodeId, $"AnimationCurveNode::{binding.PropertyName}", string.Empty]));
            objects.Add(
                Curve(
                    binding.CurveId,
                    binding.KeyTimes,
                    binding.KeyValues));
            connections.Add(Connection("OO", binding.CurveNodeId, 100));
            connections.Add(
                Connection(
                    "OP",
                    binding.CurveNodeId,
                    1,
                    binding.PropertyName));
            connections.Add(
                Connection(
                    "OP",
                    binding.CurveId,
                    binding.CurveNodeId,
                    $"d|{binding.Axis}"));
        }

        return Document(
            objects.ToArray(),
            connections.ToArray(),
            GlobalSettings(
                Property70("UnitScaleFactor", 1.0),
                Property70("CoordAxis", 0),
                Property70("CoordAxisSign", 1),
                Property70("UpAxis", 1),
                Property70("UpAxisSign", 1),
                Property70("FrontAxis", 2),
                Property70("FrontAxisSign", 1),
                Property70("TimeMode", timeMode)));
    }

    private static CurveBindingSpec CurveBinding(
        long curveNodeId,
        long curveId,
        string propertyName,
        char axis,
        long[] keyTimes,
        double[] keyValues) =>
        new(curveNodeId, curveId, propertyName, axis, keyTimes, keyValues);

    private static FbxNode Model(
        long objectId,
        string name,
        string subtype,
        params FbxNode[] properties) =>
        Node(
            "Model",
            [objectId, $"Model::{name}", subtype],
            Node(
                "Properties70",
                [],
                properties.Length == 0
                    ?
                    [
                        Property70("Lcl Translation", 0.0, 0.0, 0.0),
                        Property70("Lcl Rotation", 0.0, 0.0, 0.0),
                        Property70("Lcl Scaling", 1.0, 1.0, 1.0),
                    ]
                    : properties));

    private static FbxNode Layer(long objectId, string name) =>
        Node(
            "AnimationLayer",
            [objectId, $"AnimLayer::{name}", string.Empty]);

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

    private static FbxNode Curve(
        long objectId,
        long[] keyTimes,
        double[] keyValues) =>
        Node(
            "AnimationCurve",
            [objectId, $"AnimationCurve::{objectId}", string.Empty],
            Node("KeyTime", [keyTimes.ToImmutableArray()]),
            Node("KeyValueFloat", [keyValues.ToImmutableArray()]));

    private static FbxNode BindPose(
        long objectId,
        params (long ObjectId, ImmutableArray<double> Matrix)[] rows)
    {
        FbxNode[] children =
        [
            Node("Type", ["BindPose"]),
            .. rows.Select(
                static row =>
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

    private static ImmutableArray<double> FbxRowVectorTranslationMatrix(
        double x,
        double y,
        double z) =>
        ImmutableArray.Create(
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            x, y, z, 1.0);

    private static FbxNode GlobalSettings(params FbxNode[] properties) =>
        Node(
            "GlobalSettings",
            [],
            Node("Properties70", [], properties));

    private static FbxNode Property70(string name, params object[] values) =>
        Node(
            "P",
            [name, name, string.Empty, "A", .. values]);

    private static FbxNode Connection(
        string kind,
        long childId,
        long parentId,
        params object[] metadata) =>
        Node(
            "C",
            [kind, childId, parentId, .. metadata]);

    private static FbxBinaryDocument Document(
        FbxNode[] objects,
        FbxNode[] connections,
        FbxNode? globalSettings = null,
        FbxNode? takes = null)
    {
        var nodes = ImmutableArray.CreateBuilder<FbxNode>();
        if (globalSettings is not null)
        {
            nodes.Add(globalSettings);
        }

        nodes.Add(Node("Objects", [], objects));
        nodes.Add(Node("Connections", [], connections));
        if (takes is not null)
        {
            nodes.Add(takes);
        }

        return new FbxBinaryDocument(7400, nodes.ToImmutable());
    }

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
                ImmutableArray<long> => 'l',
                ImmutableArray<double> => 'd',
                _ => 'R',
            },
            value);

    private static void AssertMatrixLinearNear(
        TransformMatrix expected,
        TransformMatrix actual,
        double tolerance)
    {
        Assert.InRange(Math.Abs(expected.M11 - actual.M11), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.M12 - actual.M12), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.M13 - actual.M13), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.M21 - actual.M21), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.M22 - actual.M22), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.M23 - actual.M23), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.M31 - actual.M31), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.M32 - actual.M32), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.M33 - actual.M33), 0.0, tolerance);
    }

    private static void AssertVectorNear(
        Vector3D expected,
        Vector3D actual,
        double tolerance = 1e-10)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0.0, tolerance);
    }

    private sealed record CurveBindingSpec(
        long CurveNodeId,
        long CurveId,
        string PropertyName,
        char Axis,
        long[] KeyTimes,
        double[] KeyValues);
}

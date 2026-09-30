using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class FbxExtraModelPreservationTests
{
    [Fact]
    public void InertSceneLightRemainsInSourceWithoutBecomingACharacterBone()
    {
        byte[] source = BlenderFbxStrictValidationTests
            .CreateModelWithSceneLightFixture();
        FbxModelAuthoringImportResult baseline = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(),
            "generic-baseline.fbx");
        FbxModelAuthoringImportResult imported =
            FbxModelAuthoringImporter.Import(source, "generic-with-light.fbx");

        Assert.Equal(
            baseline.Package.Document.Bones.Select(static bone => bone.Name),
            imported.Package.Document.Bones.Select(static bone => bone.Name));
        Assert.Equal(baseline.Surfaces.Length, imported.Surfaces.Length);
        Assert.True(source.AsSpan().SequenceEqual(imported.Package.SourceFbx.AsSpan()));
        Assert.Contains(imported.Package.Document.Diagnostics,
            static diagnostic => diagnostic.Code == "model_scene_light_source_only" &&
                diagnostic.Subject == "SceneLight");
    }

    [Fact]
    public void AnimatedSceneLightIsNotSilentlyDropped()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateModelWithSceneLightFixture(
                    animateLight: true),
                "generic-animated-light.fbx"));

        Assert.Contains("unsupported non-Limb subtype 'Light'", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SupportedNonLimbModelsRetainIdentityHierarchyKindsAndTracks()
    {
        FbxBinaryDocument document = CreateFixture();

        FbxCoreAnimationImportResult result = FbxCoreAnimationAdapter.Import(
            document,
            new FbxCoreAnimationImportOptions
            {
                IncludeSupportedNonLimbModels = true,
                MaximumSampleFrames = 4,
            });

        Assert.Equal(
            ["Root", "RigHelper", "ReviewCamera", "MarkerNode", "PropHolder"],
            result.Rig.Bones.Select(static bone => bone.Name));
        Assert.Equal(
            [-1, 0, 0, 1, 3],
            result.Rig.Bones.Select(static bone => bone.ParentIndex));
        Assert.Equal(
            [BoneKind.Root, BoneKind.Helper, BoneKind.Camera, BoneKind.Helper, BoneKind.Prop],
            result.Rig.Bones.Select(static bone => bone.Kind));

        Assert.Equal(
            [1L, 2L, 4L, 3L, 5L],
            result.Rig.Bones.Select(bone =>
                result.Scene.Models.Values.Single(model => model.Name == bone.Name).ObjectId));
        Assert.Equal(5, result.Clip.TransformTracks.Length);
        TransformTrack helperTrack = result.Clip.TransformTracks.Single(track => track.BoneIndex == 1);
        Assert.Equal(2, helperTrack.Keyframes.Length);
        Assert.Equal(0.0, helperTrack.Keyframes[0].Value.Translation.X, 8);
        Assert.Equal(0.5, helperTrack.Keyframes[1].Value.Translation.X, 8);
    }

    [Fact]
    public void AnimatedStructuralRootIsExcludedButInheritedMotionReachesChildTrack()
    {
        FbxCoreAnimationImportResult result = FbxCoreAnimationAdapter.Import(
            CreateStructuralContainerFixture(),
            new FbxCoreAnimationImportOptions
            {
                IncludeSupportedNonLimbModels = true,
                MaximumSampleFrames = 4,
            });

        Assert.Equal(["Root", "Child"], result.Rig.Bones.Select(static bone => bone.Name));
        Assert.Equal(2, result.Clip.TransformTracks.Length);
        TransformTrack rootTrack = result.Clip.TransformTracks.Single(track => track.BoneIndex == 0);
        TransformTrack childTrack = result.Clip.TransformTracks.Single(track => track.BoneIndex == 1);
        Assert.Equal(0.0, rootTrack.Keyframes[0].Value.Translation.X, 8);
        Assert.Equal(0.5, rootTrack.Keyframes[1].Value.Translation.X, 8);
        Assert.Equal(0.0, childTrack.Keyframes[1].Value.Translation.X, 8);
    }

    [Fact]
    public async Task AuthoringImportAndSourceWriterKeepStructuralContainerOutOfLegacyBoneTable()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(),
            "connected-helper.fbx");

        Assert.DoesNotContain(
            imported.Package.Document.Bones,
            static bone => bone.FbxObjectId == 1);
        Assert.Equal("Root", imported.Package.Document.Bones[0].Name);
        Assert.Equal(BoneKind.Root, imported.Package.Document.Bones[0].Kind);
        Assert.Contains(
            imported.Package.Document.Diagnostics,
            static diagnostic => diagnostic.Code == "model_structural_container_excluded");
        Assert.NotEmpty(imported.Surfaces);
        Assert.Contains(imported.Surfaces, static surface => surface.IsSkinned);

        string outputDirectory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            Dl1SourceModelBuildResult build = await Dl1SourceModelWriter.WriteAsync(
                new Dl1SourceModelBuildRequest
                {
                    Model = imported,
                    OutputDirectory = outputDirectory,
                    ResourceName = "connected_helper",
                });
            Dl1ChrV4Document character = Dl1ChrV4Codec.Parse(
                await File.ReadAllBytesAsync(build.CharacterDefinitionPath));
            Assert.Contains("Root", character.ObjectNames);
            Assert.True(File.Exists(build.SourceMshPath));
            Assert.True(new FileInfo(build.SourceMshPath).Length > 0);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(outputDirectory);
        }
    }

    private static FbxBinaryDocument CreateFixture()
    {
        FbxNode[] models =
        [
            Model(1, "Root", "LimbNode", Translation(0.0, 0.0, 0.0)),
            Model(2, "RigHelper", "Null", Translation(0.0, 0.0, 0.0)),
            Model(3, "MarkerNode", "Marker", Translation(0.0, 0.0, 0.0)),
            Model(4, "ReviewCamera", "Camera", Translation(0.0, 0.0, 0.0)),
            Model(5, "PropHolder", "Prop", Translation(0.0, 0.0, 0.0)),
            Stack(40, "Take", 0, FbxBinaryDocument.TicksPerSecond / 30),
            Layer(100, "Base"),
            Node(
                "AnimationCurveNode",
                [200L, "AnimationCurveNode::RigHelper", string.Empty]),
            Node(
                "AnimationCurve",
                [201L, "AnimationCurve::RigHelper", string.Empty],
                Node("KeyTime", [ImmutableArray.Create(0L, FbxBinaryDocument.TicksPerSecond / 30)]),
                Node("KeyValueFloat", [ImmutableArray.Create(0.0, 50.0)])),
        ];
        FbxNode[] connections =
        [
            Connection("OO", 2, 1),
            Connection("OO", 3, 2),
            Connection("OO", 4, 1),
            Connection("OO", 5, 3),
            Connection("OO", 100, 40),
            Connection("OO", 200, 100),
            Connection("OP", 200, 2, "Lcl Translation"),
            Connection("OP", 201, 200, "d|X"),
        ];

        return new FbxBinaryDocument(
            7400,
            [
                GlobalSettings(
                    Property70("UnitScaleFactor", 1.0),
                    Property70("CoordAxis", 0),
                    Property70("CoordAxisSign", 1),
                    Property70("UpAxis", 1),
                    Property70("UpAxisSign", 1),
                    Property70("FrontAxis", 2),
                    Property70("FrontAxisSign", 1),
                    Property70("TimeMode", 11)),
                Node("Objects", [], models),
                Node("Connections", [], connections),
            ]);
    }

    private static FbxBinaryDocument CreateStructuralContainerFixture()
    {
        long stop = FbxBinaryDocument.TicksPerSecond / 30;
        FbxNode[] models =
        [
            Model(10, "Container", "Null", Translation(0.0, 0.0, 0.0)),
            Model(11, "Root", "LimbNode", Translation(0.0, 0.0, 0.0)),
            Model(12, "Child", "LimbNode", Translation(0.0, 0.0, 0.0)),
            Stack(40, "Take", 0, stop),
            Layer(100, "Base"),
            Node("AnimationCurveNode", [200L, "AnimationCurveNode::Container", string.Empty]),
            Node(
                "AnimationCurve",
                [201L, "AnimationCurve::Container", string.Empty],
                Node("KeyTime", [ImmutableArray.Create(0L, stop)]),
                Node("KeyValueFloat", [ImmutableArray.Create(0.0, 50.0)])),
        ];
        FbxNode[] connections =
        [
            Connection("OO", 11, 10),
            Connection("OO", 12, 11),
            Connection("OO", 100, 40),
            Connection("OO", 200, 100),
            Connection("OP", 200, 10, "Lcl Translation"),
            Connection("OP", 201, 200, "d|X"),
        ];
        return new FbxBinaryDocument(
            7400,
            [
                GlobalSettings(
                    Property70("UnitScaleFactor", 1.0),
                    Property70("CoordAxis", 0),
                    Property70("CoordAxisSign", 1),
                    Property70("UpAxis", 1),
                    Property70("UpAxisSign", 1),
                    Property70("FrontAxis", 2),
                    Property70("FrontAxisSign", 1),
                    Property70("TimeMode", 11)),
                Node("Objects", [], models),
                Node("Connections", [], connections),
            ]);
    }

    private static FbxNode Model(long id, string name, string subtype, FbxNode translation) =>
        Node(
            "Model",
            [id, $"Model::{name}", subtype],
            Node("Properties70", [], translation, Property70("Lcl Rotation", 0.0, 0.0, 0.0), Property70("Lcl Scaling", 1.0, 1.0, 1.0)));

    private static FbxNode Translation(double x, double y, double z) =>
        Property70("Lcl Translation", x, y, z);

    private static FbxNode Stack(long id, string name, long start, long stop) =>
        Node(
            "AnimationStack",
            [id, $"AnimStack::{name}", string.Empty],
            Node("Properties70", [], Property70("LocalStart", start), Property70("LocalStop", stop)));

    private static FbxNode Layer(long id, string name) =>
        Node("AnimationLayer", [id, $"AnimLayer::{name}", string.Empty]);

    private static FbxNode Connection(string kind, long child, long parent, params object[] metadata) =>
        Node("C", [kind, child, parent, .. metadata]);

    private static FbxNode GlobalSettings(params FbxNode[] properties) =>
        Node("GlobalSettings", [], Node("Properties70", [], properties));

    private static FbxNode Property70(string name, params object[] values) =>
        Node("P", [name, name, string.Empty, "A", .. values]);

    private static FbxNode Node(string name, object[] properties, params FbxNode[] children) =>
        new(
            name,
            properties.Select(static value => new FbxProperty(
                value switch
                {
                    long => 'L',
                    int => 'I',
                    double => 'D',
                    string => 'S',
                    ImmutableArray<long> => 'l',
                    ImmutableArray<double> => 'd',
                    _ => 'R',
                },
                value)).ToImmutableArray(),
            children.ToImmutableArray(),
            0,
            0);
}

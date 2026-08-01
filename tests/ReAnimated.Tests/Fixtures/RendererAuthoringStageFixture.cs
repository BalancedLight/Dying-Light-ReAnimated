using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fed;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting.Ik;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests.Fixtures;

/// <summary>
/// Generated geometry and animation inputs for the renderer authoring-stage
/// golden matrix. The fixture is deliberately simple enough to inspect by
/// eye and contains no data copied from a Dying Light installation.
/// </summary>
internal static class RendererAuthoringStageFixture
{
    public const int RootBoneIndex = 0;
    public const int UpperArmBoneIndex = 1;
    public const int LowerArmBoneIndex = 2;
    public const int HandBoneIndex = 3;
    public const int HelperBoneIndex = 4;
    public const int EyeCameraBoneIndex = 5;
    public const int ReferenceCameraBoneIndex = 6;
    public const int PropBoneIndex = 7;

    public static readonly Guid AttachmentAssetId =
        Guid.Parse("21f73969-69e0-4fc5-a10e-e30d515d02de");

    public static RenderCamera OrbitCamera { get; } = new(
        new Vector3(0.52f, 0.18f, 3.25f),
        new Vector3(0.52f, 0.18f, 0.0f),
        Vector3.UnitY,
        50.0f,
        0.02f,
        100.0f);

    public static Vector3D IkTarget { get; } =
        new(0.68, 0.83, 0.0);

    /// <summary>
    /// DL1 InPlace ownership runs after authored IK. The generated clip's
    /// planar root displacement is therefore removed from the solved chain.
    /// </summary>
    public static Vector3D PostRootPolicyIkTarget { get; } =
        new(0.40, 0.75, 0.0);

    public static RigDefinition CreateSourceRig() =>
        new(
            "synthetic-renderer-source-v1",
            "Synthetic renderer source",
            [
                Bone(
                    RootBoneIndex,
                    "source_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    "root.skeletal"),
                Bone(
                    UpperArmBoneIndex,
                    "source_upper",
                    RootBoneIndex,
                    Translation(0.0, 0.18, 0.0),
                    BoneKind.Deform,
                    "arm.right.upper"),
                Bone(
                    LowerArmBoneIndex,
                    "source_lower",
                    UpperArmBoneIndex,
                    Translation(0.55, 0.0, 0.0),
                    BoneKind.Deform,
                    "arm.right.lower"),
                Bone(
                    HandBoneIndex,
                    "source_hand",
                    LowerArmBoneIndex,
                    Translation(0.45, 0.0, 0.0),
                    BoneKind.Deform,
                    "hand.right"),
            ]);

    public static RigDefinition CreateTargetRig() =>
        new(
            "synthetic-renderer-target-v1",
            "Synthetic renderer target",
            [
                Bone(
                    RootBoneIndex,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    "root.skeletal"),
                Bone(
                    UpperArmBoneIndex,
                    "target_upper",
                    RootBoneIndex,
                    Translation(0.0, 0.20, 0.0),
                    BoneKind.Deform,
                    "arm.right.upper"),
                Bone(
                    LowerArmBoneIndex,
                    "target_lower",
                    UpperArmBoneIndex,
                    Translation(0.65, 0.0, 0.0),
                    BoneKind.Deform,
                    "arm.right.lower"),
                Bone(
                    HandBoneIndex,
                    "target_hand",
                    LowerArmBoneIndex,
                    Translation(0.55, 0.0, 0.0),
                    BoneKind.Deform,
                    "hand.right"),
                Bone(
                    HelperBoneIndex,
                    "authoring_helper",
                    HandBoneIndex,
                    Translation(0.18, 0.10, 0.0),
                    BoneKind.Helper,
                    "helper.authoring",
                    requiredForExport: false),
                Bone(
                    EyeCameraBoneIndex,
                    Dl1PreviewContract.EyeCameraBoneName,
                    RootBoneIndex,
                    Translation(0.50, 0.15, -1.40),
                    BoneKind.Camera,
                    Dl1PreviewContract.EyeCameraSemanticRole,
                    requiredForExport: false),
                Bone(
                    ReferenceCameraBoneIndex,
                    Dl1PreviewContract.ReferenceCameraBoneName,
                    RootBoneIndex,
                    Translation(0.50, 0.20, -1.20),
                    BoneKind.Helper,
                    Dl1PreviewContract.ReferenceCameraSemanticRole,
                    requiredForExport: false),
                Bone(
                    PropBoneIndex,
                    "weapon_socket",
                    HandBoneIndex,
                    Translation(0.12, -0.10, 0.0),
                    BoneKind.Prop,
                    "prop.right_hand",
                    requiredForExport: false),
            ],
            [
                new MorphChannelDefinition(
                    0,
                    "smile",
                    descriptorHash: 0x10000001,
                    semanticRole: "face.smile"),
                new MorphChannelDefinition(
                    1,
                    "jaw_drop",
                    descriptorHash: 0x10000002,
                    semanticRole: "face.jaw.drop"),
            ],
            ikChains:
            [
                new TwoBoneIkChainDefinition(
                    "right_hand",
                    UpperArmBoneIndex,
                    LowerArmBoneIndex,
                    HandBoneIndex),
            ]);

    public static RetargetMap CreateMapping(
        RigDefinition sourceRig,
        RigDefinition targetRig) =>
        new(
            sourceRig.Id,
            targetRig.Id,
            [
                Map(RootBoneIndex, RootBoneIndex),
                Map(UpperArmBoneIndex, UpperArmBoneIndex),
                Map(LowerArmBoneIndex, LowerArmBoneIndex),
                Map(HandBoneIndex, HandBoneIndex),
            ],
            reviewedTargetBindBoneIndices:
            [
                HelperBoneIndex,
                EyeCameraBoneIndex,
                ReferenceCameraBoneIndex,
                PropBoneIndex,
            ]);

    public static AnimationClip CreateClip(
        RigDefinition sourceRig,
        bool includeAuthoredMorph)
    {
        TransformTRS upperBind =
            sourceRig.Bones[UpperArmBoneIndex].LocalBindPose;
        TransformTRS lowerBind =
            sourceRig.Bones[LowerArmBoneIndex].LocalBindPose;
        return new AnimationClip(
            includeAuthoredMorph
                ? "synthetic_authoring_with_morph"
                : "synthetic_authoring_body",
            new FrameRate(30, 1),
            2,
            [
                new TransformTrack(
                    RootBoneIndex,
                    [
                        new TransformKeyframe(0, TransformTRS.Identity),
                        new TransformKeyframe(
                            1,
                            Translation(0.28, 0.08, 0.0)),
                    ]),
                new TransformTrack(
                    UpperArmBoneIndex,
                    [
                        new TransformKeyframe(0, upperBind),
                        new TransformKeyframe(
                            1,
                            upperBind with
                            {
                                Rotation = RotationDegrees(12),
                            }),
                    ]),
                new TransformTrack(
                    LowerArmBoneIndex,
                    [
                        new TransformKeyframe(0, lowerBind),
                        new TransformKeyframe(
                            1,
                            lowerBind with
                            {
                                Rotation = RotationDegrees(-8),
                            }),
                    ]),
            ],
            includeAuthoredMorph
                ?
                [
                    new ScalarTrack(
                        "smile",
                        [
                            new ScalarKeyframe(0, 0.0),
                            new ScalarKeyframe(1, 0.55),
                        ]),
                ]
                : null);
    }

    public static BoneEditLayer CreateBoneEditLayer() =>
        new(
            Guid.Parse("dc5a0362-6f93-40f3-a6f7-a1287deac905"),
            "Synthetic upper-arm correction",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.AuthoredExportable,
            1.0,
            [
                new BoneEditTrack(
                    UpperArmBoneIndex,
                    [
                        new TransformKeyframe(
                            0,
                            new TransformTRS(
                                Vector3D.Zero,
                                RotationDegrees(24),
                                Vector3D.One)),
                    ]),
            ]);

    public static IkConstraintLayer CreateIkLayer() =>
        new(
            Guid.Parse("0ce72149-fe2f-48f4-8ddb-066a04ca90b0"),
            "Synthetic right-hand placement",
            UpperArmBoneIndex,
            LowerArmBoneIndex,
            HandBoneIndex,
            1.0,
            [
                new IkConstraintKeyframe(
                    0,
                    IkTarget,
                    new Vector3D(0.25, 0.30, 0.75)),
            ]);

    public static FedLayerBuildResult CreateFedLayer(
        RigDefinition targetRig)
    {
        var document = new FedDocument(
            "synthetic_facial_expressions",
            [
                new FedExpression(
                    "open_jaw",
                    [
                        new FedMorphWeight("fed_jaw", 0.70f),
                    ]),
            ],
            []);
        return FedDomainAdapter.CreateLayer(
            document,
            "open_jaw",
            targetRig,
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["fed_jaw"] = "jaw_drop",
            });
    }

    public static AttachmentBinding CreateAttachmentBinding() =>
        new(
            Guid.Parse("9d24b4d7-b610-4242-8dfc-c4f0f1fd39de"),
            AttachmentAssetId,
            "Synthetic hand prop",
            HandBoneIndex,
            new TransformTRS(
                new Vector3D(0.14, -0.18, 0.01),
                RotationDegrees(-18),
                Vector3D.One),
            AttachmentScope.AuthoredExportable,
            "target_hand");

    public static AttachmentRenderAsset CreateAttachmentAsset() =>
        new(
            AttachmentAssetId,
            "Synthetic hand prop",
            [CreatePropMesh()],
            null);

    public static MeshRenderData CreateActorMesh(
        RigDefinition targetRig)
    {
        ArgumentNullException.ThrowIfNull(targetRig);
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        AddQuad(
            vertices,
            indices,
            RootBoneIndex,
            -0.28f,
            -0.35f,
            0.28f,
            0.30f);
        int headStart = vertices.Count;
        AddQuad(
            vertices,
            indices,
            RootBoneIndex,
            -0.22f,
            0.30f,
            0.22f,
            0.72f);
        AddQuad(
            vertices,
            indices,
            UpperArmBoneIndex,
            -0.04f,
            0.14f,
            0.68f,
            0.26f);
        AddQuad(
            vertices,
            indices,
            LowerArmBoneIndex,
            0.62f,
            0.14f,
            1.22f,
            0.26f);
        AddQuad(
            vertices,
            indices,
            HandBoneIndex,
            1.14f,
            0.06f,
            1.36f,
            0.34f);

        Vector3[] smileDeltas = new Vector3[vertices.Count];
        smileDeltas[headStart + 2] = new Vector3(0.16f, 0.02f, 0.0f);
        smileDeltas[headStart + 3] = new Vector3(-0.16f, 0.02f, 0.0f);
        Vector3[] jawDeltas = new Vector3[vertices.Count];
        jawDeltas[headStart] = new Vector3(-0.03f, -0.20f, 0.0f);
        jawDeltas[headStart + 1] = new Vector3(0.03f, -0.20f, 0.0f);

        SkeletonPose bindPose = targetRig.CreateBindPose();
        Matrix4x4[] inverseBinds = bindPose.GlobalMatrices
            .Select(static transform =>
                CorePreviewAdapter.ToSystemMatrix(
                    transform.InvertedAffine()))
            .ToArray();
        return new MeshRenderData(
            "synthetic-authoring-actor",
            vertices.ToArray(),
            indices.ToArray(),
            Matrix4x4.Identity,
            inverseBinds,
            IsSkinned: true)
        {
            Tint = new Vector4(0.45f, 0.62f, 0.82f, 1.0f),
            MorphTargets =
            [
                new MorphTargetRenderData(
                    "smile",
                    smileDeltas,
                    ReadOnlyMemory<Vector3>.Empty),
                new MorphTargetRenderData(
                    "jaw_drop",
                    jawDeltas,
                    ReadOnlyMemory<Vector3>.Empty),
            ],
        };
    }

    public static MeshRenderData CreateFppHandsMesh()
    {
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        AddStaticQuadFacingNegativeZ(
            vertices,
            indices,
            0.24f,
            -0.02f,
            0.44f,
            0.12f,
            -0.45f);
        AddStaticQuadFacingNegativeZ(
            vertices,
            indices,
            0.56f,
            -0.02f,
            0.76f,
            0.12f,
            -0.45f);
        return new MeshRenderData(
            "synthetic-fpp-hands",
            vertices.ToArray(),
            indices.ToArray(),
            Matrix4x4.Identity,
            ReadOnlyMemory<Matrix4x4>.Empty,
            IsSkinned: false)
        {
            Tint = new Vector4(0.82f, 0.48f, 0.28f, 1.0f),
            ProjectionRole = MeshProjectionRole.FppHands,
        };
    }

    private static MeshRenderData CreatePropMesh()
    {
        MeshVertex[] vertices =
        [
            StaticVertex(new Vector3(-0.08f, -0.30f, 0.0f)),
            StaticVertex(new Vector3(0.08f, -0.30f, 0.0f)),
            StaticVertex(new Vector3(0.08f, 0.30f, 0.0f)),
            StaticVertex(new Vector3(-0.08f, 0.30f, 0.0f)),
        ];
        return new MeshRenderData(
            "synthetic-prop",
            vertices,
            new uint[] { 0, 1, 2, 0, 2, 3 },
            Matrix4x4.Identity,
            ReadOnlyMemory<Matrix4x4>.Empty,
            IsSkinned: false)
        {
            Tint = new Vector4(0.82f, 0.67f, 0.22f, 1.0f),
        };
    }

    private static BoneDefinition Bone(
        int index,
        string name,
        int parentIndex,
        TransformTRS bind,
        BoneKind kind,
        string semanticRole,
        bool requiredForExport = true) =>
        new(
            index,
            name,
            parentIndex,
            bind,
            kind,
            requiredForExport,
            descriptorHash: null,
            semanticRole);

    private static BoneMapEntry Map(
        int sourceBoneIndex,
        int targetBoneIndex) =>
        new(
            sourceBoneIndex,
            targetBoneIndex,
            BoneMappingMethod.Manual,
            1.0,
            isLocked: true,
            isReviewed: true);

    private static TransformTRS Translation(
        double x,
        double y,
        double z) =>
        new(
            new Vector3D(x, y, z),
            QuaternionD.Identity,
            Vector3D.One);

    private static QuaternionD RotationDegrees(double degrees) =>
        QuaternionD.FromAxisAngle(
            Vector3D.UnitZ,
            degrees * Math.PI / 180.0);

    private static void AddQuad(
        List<MeshVertex> vertices,
        List<uint> indices,
        int boneIndex,
        float left,
        float bottom,
        float right,
        float top)
    {
        uint first = checked((uint)vertices.Count);
        vertices.Add(WeightedVertex(
            new Vector3(left, bottom, 0.0f),
            boneIndex));
        vertices.Add(WeightedVertex(
            new Vector3(right, bottom, 0.0f),
            boneIndex));
        vertices.Add(WeightedVertex(
            new Vector3(right, top, 0.0f),
            boneIndex));
        vertices.Add(WeightedVertex(
            new Vector3(left, top, 0.0f),
            boneIndex));
        indices.AddRange(
        [
            first,
            first + 1,
            first + 2,
            first,
            first + 2,
            first + 3,
        ]);
    }

    private static void AddStaticQuadFacingNegativeZ(
        List<MeshVertex> vertices,
        List<uint> indices,
        float left,
        float bottom,
        float right,
        float top,
        float z)
    {
        uint first = checked((uint)vertices.Count);
        vertices.Add(StaticVertex(
            new Vector3(left, bottom, z),
            -Vector3.UnitZ));
        vertices.Add(StaticVertex(
            new Vector3(right, bottom, z),
            -Vector3.UnitZ));
        vertices.Add(StaticVertex(
            new Vector3(right, top, z),
            -Vector3.UnitZ));
        vertices.Add(StaticVertex(
            new Vector3(left, top, z),
            -Vector3.UnitZ));
        indices.AddRange(
        [
            first,
            first + 2,
            first + 1,
            first,
            first + 3,
            first + 2,
        ]);
    }

    private static MeshVertex WeightedVertex(
        Vector3 position,
        int boneIndex) =>
        new(
            position,
            Vector3.UnitZ,
            Vector2.Zero,
            Vector4.UnitX,
            new Vector4(boneIndex, 0.0f, 0.0f, 0.0f));

    private static MeshVertex StaticVertex(
        Vector3 position,
        Vector3? normal = null) =>
        new(
            position,
            normal ?? Vector3.UnitZ,
            Vector2.Zero,
            Vector4.Zero,
            Vector4.Zero);
}

using System.Collections.Immutable;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests.Fixtures;

/// <summary>
/// Entirely generated, redistributable DL1-shaped inputs for the authoring
/// regression. Nothing in this fixture was copied from a retail game asset.
/// </summary>
internal static class Dl1AuthoringEndToEndFixture
{
    public const string RetailResourceName = "dlr_e2e_survivor";
    public const string BodyAnimationName = "dlr_e2e_body";
    public const string MimicAnimationName = "dlr_e2e_mimic";
    public const string AnimationScriptName = "anims_dlr_e2e";
    public const string PreviewStageId = "dlr-e2e-preview-root-offset";

    public static IReadOnlyList<RpackTestItem> CreateRetailMeshItems()
    {
        CompiledMeshTestFixture fixture =
            RpackTestData.BuildCompiledMeshFixture();
        return
        [
            new RpackTestItem(42, fixture.Metadata),
            new RpackTestItem(42, fixture.Variants),
            new RpackTestItem(42, [0x44, 0x4C, 0x52]),
            new RpackTestItem(42, fixture.Vertices),
            new RpackTestItem(42, fixture.Indices),
        ];
    }

    public static RigDefinition BindRetailIdentity(
        Dl1MeshData decoded,
        string contentSha256,
        string resourceId)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        RigDefinition rig = decoded.Rig ??
            throw new InvalidDataException(
                "The generated retail fixture did not decode an authoring rig.");
        var fingerprint = new SourceAssetFingerprint(
            $"packs/dlc/{RetailResourceName}.rpack/{RetailResourceName}",
            contentSha256,
            resourceId);
        return new RigDefinition(
            rig.Id,
            rig.DisplayName,
            rig.Bones,
            rig.MorphChannels,
            fingerprint,
            rig.IkChains);
    }

    public static SyntheticAnimationImport CreateImportedAnimations(
        RigDefinition targetRig)
    {
        ArgumentNullException.ThrowIfNull(targetRig);
        var sourceRig = new RigDefinition(
            "synthetic-source:authoring-e2e",
            "Synthetic source",
            [
                new BoneDefinition(
                    0,
                    "source_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: Dl1NameHash.Compute("source_root"),
                    semanticRole: "body.root"),
            ]);
        var frameRate = new FrameRate(30, 1);
        var bodySeed = new AnimationClip(
            BodyAnimationName,
            frameRate,
            3,
            [
                new TransformTrack(
                    0,
                    [
                        Key(0, 0),
                        Key(1, 1),
                        Key(2, 2),
                    ]),
            ]);
        MorphChannelDefinition smile = AssertSingleSmile(targetRig);
        var mimicSeed = new AnimationClip(
            MimicAnimationName,
            frameRate,
            3,
            scalarTracks:
            [
                new ScalarTrack(
                    smile.Name,
                    [
                        new ScalarKeyframe(0, 0.1),
                        new ScalarKeyframe(1, 0.6),
                        new ScalarKeyframe(2, 0.2),
                    ]),
            ]);

        uint sourceDescriptor =
            sourceRig.Bones[0].DescriptorHash!.Value;
        uint mimicDescriptor =
            smile.DescriptorHash ??
            throw new InvalidDataException(
                "The generated morph channel has no DL1 descriptor.");
        byte[] bodyBytes = Anm2DomainAdapter.ExportBody(
            bodySeed,
            sourceRig,
            [sourceDescriptor]);
        byte[] mimicBytes = Anm2DomainAdapter.ExportMimic(
            mimicSeed,
            targetRig,
            [mimicDescriptor]);
        Anm2DomainImportResult bodyImport =
            Anm2DomainAdapter.ImportBody(
                Anm2Reader.Read(bodyBytes, $"{BodyAnimationName}.anm2"),
                sourceRig,
                frameRate);
        AnimationClip mimicImport =
            Anm2DomainAdapter.ImportMimic(
                Anm2Reader.Read(
                    mimicBytes,
                    $"{MimicAnimationName}.anm2"),
                targetRig,
                frameRate);
        return new SyntheticAnimationImport(
            sourceRig,
            bodyImport,
            mimicImport,
            bodyBytes,
            mimicBytes);
    }

    public static ImmutableArray<BoneEditLayer> CreateBoneEditLayers() =>
    [
        new BoneEditLayer(
            Guid.Parse("119cc9ca-165d-4e9e-a80c-cc31ad6471f3"),
            "Authored root correction",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.AuthoredExportable,
            1,
            [
                new BoneEditTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0,
                            Translation(0.25)),
                    ]),
            ]),
        new BoneEditLayer(
            Guid.Parse("1bf8a16f-ad8e-4c1f-a91e-b6419ff7f41e"),
            "Preview framing override",
            BoneEditBlendMode.Override,
            BoneEditLayerScope.PreviewOnly,
            1,
            [
                new BoneEditTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0,
                            Translation(99)),
                    ]),
            ]),
    ];

    public static ImmutableArray<MorphEditLayer> CreateMorphEditLayers() =>
    [
        new MorphEditLayer(
            Guid.Parse("368c8720-92c6-40dc-b1b0-1bf4fa6d2a08"),
            "Authored smile correction",
            MorphEditBlendMode.Additive,
            MorphEditLayerScope.AuthoredExportable,
            1,
            [
                new MorphEditTrack(
                    "smile",
                    [new ScalarKeyframe(0, 0.1)]),
            ]),
        new MorphEditLayer(
            Guid.Parse("c7ab1586-bd2f-48c8-82b0-985d6ebf28bb"),
            "Preview neutral face",
            MorphEditBlendMode.Override,
            MorphEditLayerScope.PreviewOnly,
            1,
            [
                new MorphEditTrack(
                    "smile",
                    [new ScalarKeyframe(0, 0.0005)]),
            ]),
    ];

    public static PreviewProfile CreatePreviewProfile() =>
        new(
            "dlr_e2e_dl1_body",
            PreviewViewMode.ThirdPerson,
            AuthoringPreviewFidelity.AuthoringAccurate,
            PreviewVisualStyle.MaterialApproximation,
            null,
            CameraLens.Default,
            TransformTRS.Identity,
            PreviewFidelityTier.Dl1Profile,
            Dl1PreviewContext.Dl1Body,
            proceduralToggles: [PreviewStageId],
            morphActivationThreshold: 0.001,
            maximumActiveMorphTargets: 64);

    public static AnimationScrSections CreateAnimationScript() =>
        AnimationScrCodec.Build(
        [
            new AnimationScrSequence(
                BodyAnimationName,
                $"{BodyAnimationName}.anm2",
                0,
                2,
                30),
        ]);

    private static MorphChannelDefinition AssertSingleSmile(
        RigDefinition targetRig)
    {
        MorphChannelDefinition[] matches = targetRig.MorphChannels
            .Where(static morph =>
                string.Equals(
                    morph.Name,
                    "smile",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                "The generated retail rig must contain exactly one smile morph.");
    }

    private static TransformKeyframe Key(double frame, double x) =>
        new(frame, Translation(x));

    private static TransformTRS Translation(double x) =>
        new(
            new Vector3D(x, 0, 0),
            QuaternionD.Identity,
            Vector3D.One);
}

internal sealed record SyntheticAnimationImport(
    RigDefinition SourceRig,
    Anm2DomainImportResult Body,
    AnimationClip Mimic,
    byte[] SourceBodyBytes,
    byte[] SourceMimicBytes);

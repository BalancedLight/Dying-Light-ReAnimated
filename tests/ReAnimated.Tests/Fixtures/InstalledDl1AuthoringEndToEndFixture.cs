using System.Collections.Immutable;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests.Fixtures;

/// <summary>
/// Generated authoring inputs for the optional installed-retail regression.
/// The selected mesh is read from the user's own DL1 installation at runtime;
/// no retail payload or decoded mesh bytes are retained by this fixture.
/// </summary>
internal static class InstalledDl1AuthoringEndToEndFixture
{
    public const string BodyAnimationName = "dlr_installed_control_body";
    public const string MimicAnimationName = "dlr_installed_control_mimic";
    public const string AnimationScriptName =
        "anims_dlr_installed_control";
    public const double AuthoredBoneOffset = 0.125;
    public const double AuthoredMorphOffset = 0.05;

    public static IReadOnlyList<string> PreferredControlNames { get; } =
    [
        "armored",
        "player_1_tpp",
        "player_4_head",
        "beard",
    ];

    public static RigDefinition BindRetailIdentity(
        Dl1MeshData mesh,
        RetailAssetRecord asset,
        string installPath,
        string contentSha256)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        RigDefinition rig = mesh.Rig ??
            throw new InvalidDataException(
                $"Retail control '{asset.DisplayName}' has no decoded rig.");
        string relativePack = Path.GetRelativePath(
                installPath,
                asset.Source.ContainerPath)
            .Replace('\\', '/');
        var fingerprint = new SourceAssetFingerprint(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{relativePack}/{asset.DisplayName}#{asset.Id.SourceIndex}"),
            contentSha256,
            asset.Id.StableKey);
        return new RigDefinition(
            rig.Id,
            rig.DisplayName,
            rig.Bones,
            rig.MorphChannels,
            fingerprint,
            rig.IkChains);
    }

    public static InstalledSyntheticAnimationImport CreateImportedAnimations(
        RigDefinition targetRig,
        BoneDefinition targetBone,
        MorphChannelDefinition targetMorph)
    {
        ArgumentNullException.ThrowIfNull(targetRig);
        ArgumentNullException.ThrowIfNull(targetBone);
        ArgumentNullException.ThrowIfNull(targetMorph);
        uint sourceDescriptor =
            Dl1NameHash.Compute("dlr_installed_source_control");
        var sourceRig = new RigDefinition(
            "synthetic-source:installed-dl1-authoring-e2e",
            "Generated installed-retail source",
            [
                new BoneDefinition(
                    0,
                    "dlr_installed_source_control",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: sourceDescriptor,
                    semanticRole: targetBone.SemanticRole),
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
                        BodyKey(0, 0),
                        BodyKey(1, 0.5),
                        BodyKey(2, 1),
                    ]),
            ]);
        var mimicSeed = new AnimationClip(
            MimicAnimationName,
            frameRate,
            3,
            scalarTracks:
            [
                new ScalarTrack(
                    targetMorph.Name,
                    [
                        new ScalarKeyframe(0, 0.1),
                        new ScalarKeyframe(1, 0.4),
                        new ScalarKeyframe(2, 0.2),
                    ]),
            ]);
        uint mimicDescriptor =
            targetMorph.DescriptorHash ??
            throw new InvalidDataException(
                $"Retail morph '{targetMorph.Name}' has no DL1 descriptor.");
        byte[] bodyBytes = Anm2DomainAdapter.ExportBody(
            bodySeed,
            sourceRig,
            [sourceDescriptor]);
        byte[] mimicBytes = Anm2DomainAdapter.ExportMimic(
            mimicSeed,
            targetRig,
            [mimicDescriptor]);
        Anm2DomainImportResult body =
            Anm2DomainAdapter.ImportBody(
                Anm2Reader.Read(
                    bodyBytes,
                    $"{BodyAnimationName}.anm2"),
                sourceRig,
                frameRate);
        AnimationClip mimic = Anm2DomainAdapter.ImportMimic(
            Anm2Reader.Read(
                mimicBytes,
                $"{MimicAnimationName}.anm2"),
            targetRig,
            frameRate);
        return new InstalledSyntheticAnimationImport(
            sourceRig,
            body,
            mimic,
            bodyBytes,
            mimicBytes);
    }

    public static ImmutableArray<BoneEditLayer> CreateBoneEditLayers(
        int boneIndex) =>
    [
        new BoneEditLayer(
            Guid.Parse("9dfa1931-4b08-4e5c-bd78-e2605231bb45"),
            "Installed-retail bone correction",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.AuthoredExportable,
            1,
            [
                new BoneEditTrack(
                    boneIndex,
                    [
                        new TransformKeyframe(
                            0,
                            Translation(AuthoredBoneOffset)),
                    ]),
            ]),
    ];

    public static ImmutableArray<MorphEditLayer> CreateMorphEditLayers(
        string morphName) =>
    [
        new MorphEditLayer(
            Guid.Parse("b89e01cd-58b5-40fb-a5e4-b5b2bd52c2f6"),
            "Installed-retail facial correction",
            MorphEditBlendMode.Additive,
            MorphEditLayerScope.AuthoredExportable,
            1,
            [
                new MorphEditTrack(
                    morphName,
                    [
                        new ScalarKeyframe(
                            0,
                            AuthoredMorphOffset),
                    ]),
            ]),
    ];

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

    private static TransformKeyframe BodyKey(
        double frame,
        double translationX) =>
        new(frame, Translation(translationX));

    private static TransformTRS Translation(double x) =>
        new(
            new Vector3D(x, 0, 0),
            QuaternionD.Identity,
            Vector3D.One);
}

internal sealed record InstalledSyntheticAnimationImport(
    RigDefinition SourceRig,
    Anm2DomainImportResult Body,
    AnimationClip Mimic,
    byte[] SourceBodyBytes,
    byte[] SourceMimicBytes);

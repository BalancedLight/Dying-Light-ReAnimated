using System.Text.RegularExpressions;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.DL1.Assets.Meshes;

public interface IDl1RetailMeshClassificationService
{
    Dl1RetailMeshProfile Classify(
        RetailAssetRecord asset,
        Dl1MeshData mesh);
}

/// <summary>
/// Evidence-driven DL1-only retail mesh classification. Resource-name rules are
/// bounded hints; a family is emitted only when decoded skin and humanoid rig
/// anchors corroborate the hint.
/// </summary>
public sealed partial class Dl1RetailMeshClassificationService
    : IDl1RetailMeshClassificationService
{
    private const int MaximumClassificationNameCharacters = 4_096;

    private static readonly string[] HumanoidRequiredAnchors =
        ["bip01", "pelvis", "head"];

    private static readonly string[] HumanoidLimbAnchors =
    [
        "l_upperarm",
        "r_upperarm",
        "l_thigh",
        "r_thigh",
    ];

    public Dl1RetailMeshProfile Classify(
        RetailAssetRecord asset,
        Dl1MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(mesh);
        if (asset.Id.Namespace != RetailAssetNamespace.RpackResource ||
            asset.Id.ResourceType != Rp6lResourceTypes.Mesh)
        {
            throw new ArgumentException(
                "DL1 retail mesh classification requires a type-272 RPack asset.",
                nameof(asset));
        }

        List<Dl1ClassificationEvidence> evidence = [];
        bool identityNamesAreBounded =
            asset.Id.Name.Length <=
                MaximumClassificationNameCharacters &&
            mesh.ResourceName.Length <=
                MaximumClassificationNameCharacters;
        bool identityMatches =
            identityNamesAreBounded &&
            string.Equals(
                asset.Id.Name,
                NormalizeResourceName(mesh.ResourceName),
                StringComparison.OrdinalIgnoreCase);
        if (!identityNamesAreBounded)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "identity.resource-name-too-long",
                Dl1ClassificationEvidenceSource.ResourceIdentity,
                Dl1ClassificationConfidence.High,
                $"A resource identity exceeds the {MaximumClassificationNameCharacters}-character classification limit. Family and perspective classification were disabled."));
        }
        else if (!identityMatches)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "identity.resource-name-mismatch",
                Dl1ClassificationEvidenceSource.ResourceIdentity,
                Dl1ClassificationConfidence.High,
                $"Catalog resource '{asset.Id.Name}' does not match decoded resource '{mesh.ResourceName}'. Family and perspective classification were disabled."));
        }
        else
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "identity.resource-name-match",
                Dl1ClassificationEvidenceSource.ResourceIdentity,
                Dl1ClassificationConfidence.High,
                $"Catalog and decoded resource names agree on '{asset.Id.Name}'."));
        }

        Dl1MeshGeometryKind geometry = ClassifyGeometry(mesh, evidence);
        string? rigSignature = ClassifyRigSignature(
            mesh,
            evidence);
        (Dl1RigFamily family, Dl1ClassificationConfidence familyConfidence) =
            identityMatches
                ? ClassifyFamily(
                    asset.Id.Name,
                    mesh,
                    geometry,
                    evidence)
                : (Dl1RigFamily.Unknown, Dl1ClassificationConfidence.None);
        (Dl1MeshPerspective perspective,
            Dl1ClassificationConfidence perspectiveConfidence) =
            identityMatches
                ? ClassifyPerspective(asset.Id.Name, evidence)
                : (Dl1MeshPerspective.Unknown,
                    Dl1ClassificationConfidence.None);
        Dl1FacialSupport facial = ClassifyFacialSupport(
            mesh,
            evidence);
        (Dl1RetailSourceScope sourceScope, string? dlcIdentifier) =
            ClassifySource(asset.Source, evidence);
        string[] variants = mesh.VariantNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (variants.Length > 0)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "variants.decoded",
                Dl1ClassificationEvidenceSource.VariantTable,
                Dl1ClassificationConfidence.High,
                $"Decoded {variants.Length} distinct variant name(s)."));
        }

        return new Dl1RetailMeshProfile(
            asset.Id,
            geometry,
            rigSignature,
            family,
            familyConfidence,
            perspective,
            perspectiveConfidence,
            facial,
            asset.Source.ProviderId,
            Path.GetFileName(asset.Source.ContainerPath),
            sourceScope,
            dlcIdentifier,
            variants,
            evidence.ToArray());
    }

    private static Dl1MeshGeometryKind ClassifyGeometry(
        Dl1MeshData mesh,
        List<Dl1ClassificationEvidence> evidence)
    {
        if (mesh.IsSkinned)
        {
            int paletteCount = mesh.Surfaces
                .SelectMany(static surface => surface.Submeshes)
                .Count(static submesh =>
                    submesh.BonePaletteEntityIndexes.Count > 0);
            evidence.Add(new Dl1ClassificationEvidence(
                "geometry.skin-palettes",
                Dl1ClassificationEvidenceSource.DecodedGeometry,
                Dl1ClassificationConfidence.High,
                $"Decoded {paletteCount} submesh skin palette(s)."));
            if (!mesh.IsStructurallyValid)
            {
                evidence.Add(new Dl1ClassificationEvidence(
                    "geometry.partial-decode-errors",
                    Dl1ClassificationEvidenceSource.DecodedGeometry,
                    Dl1ClassificationConfidence.High,
                    "Positive skin-palette evidence is retained, but other mesh diagnostics still contain release-blocking errors."));
            }

            return Dl1MeshGeometryKind.Skinned;
        }

        if (!mesh.IsStructurallyValid)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "geometry.decode-errors",
                Dl1ClassificationEvidenceSource.DecodedGeometry,
                Dl1ClassificationConfidence.High,
                "Mesh diagnostics contain errors, so absence of skin palettes cannot prove a static geometry kind."));
            return Dl1MeshGeometryKind.Unknown;
        }

        if (mesh.HasDecodedGeometry)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "geometry.no-skin-palettes",
                Dl1ClassificationEvidenceSource.DecodedGeometry,
                Dl1ClassificationConfidence.High,
                "Decoded geometry has no submesh skin palettes."));
            return Dl1MeshGeometryKind.Static;
        }

        if (mesh.ContainerLayout ==
            Dl1MeshContainerLayout.ThreeItemMetadataOnly)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "geometry.metadata-container",
                Dl1ClassificationEvidenceSource.DecodedGeometry,
                Dl1ClassificationConfidence.High,
                "The resource uses the explicit three-item metadata-only container layout."));
            return Dl1MeshGeometryKind.MetadataContainer;
        }

        evidence.Add(new Dl1ClassificationEvidence(
            "geometry.not-decoded",
            Dl1ClassificationEvidenceSource.DecodedGeometry,
            Dl1ClassificationConfidence.None,
            "No decoded surface or recognized metadata-only layout proves a geometry kind."));
        return Dl1MeshGeometryKind.Unknown;
    }

    private static string? ClassifyRigSignature(
        Dl1MeshData mesh,
        List<Dl1ClassificationEvidence> evidence)
    {
        if (mesh.Rig is null)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "rig.signature-unavailable",
                Dl1ClassificationEvidenceSource.DecodedRig,
                Dl1ClassificationConfidence.None,
                "No structurally valid authoring rig is available for signature computation."));
            return null;
        }

        string signature = RigSignature.Compute(mesh.Rig);
        evidence.Add(new Dl1ClassificationEvidence(
            "rig.signature-v1",
            Dl1ClassificationEvidenceSource.DecodedRig,
            Dl1ClassificationConfidence.High,
            $"Computed {RigSignature.Algorithm} from {mesh.Rig.BoneCount} transform nodes and {mesh.Rig.MorphChannels.Length} morph channels."));
        return signature;
    }

    private static (
        Dl1RigFamily Family,
        Dl1ClassificationConfidence Confidence)
        ClassifyFamily(
            string resourceName,
            Dl1MeshData mesh,
            Dl1MeshGeometryKind geometry,
            List<Dl1ClassificationEvidence> evidence)
    {
        Dl1RigFamily hintedFamily =
            GetFamilyNameHint(resourceName);
        if (hintedFamily == Dl1RigFamily.Unknown)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "family.no-bounded-name-rule",
                Dl1ClassificationEvidenceSource.ResourceIdentity,
                Dl1ClassificationConfidence.None,
                "The resource name does not match a bounded DL1 family rule."));
            return (Dl1RigFamily.Unknown, Dl1ClassificationConfidence.None);
        }

        evidence.Add(new Dl1ClassificationEvidence(
            $"family.name-hint.{ToStableToken(hintedFamily)}",
            Dl1ClassificationEvidenceSource.ResourceIdentity,
            Dl1ClassificationConfidence.Low,
            $"The normalized resource name is a bounded hint for {hintedFamily}."));

        if (geometry != Dl1MeshGeometryKind.Skinned ||
            mesh.Rig is null)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "family.hint-not-corroborated",
                Dl1ClassificationEvidenceSource.DecodedRig,
                Dl1ClassificationConfidence.High,
                "A family name hint was present, but decoded skin and rig evidence did not corroborate it."));
            return (Dl1RigFamily.Unknown, Dl1ClassificationConfidence.None);
        }

        HashSet<string> boneNames = mesh.Rig.Bones
            .Select(static bone => bone.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] missingRequired = HumanoidRequiredAnchors
            .Where(anchor => !boneNames.Contains(anchor))
            .ToArray();
        int limbAnchorCount = HumanoidLimbAnchors
            .Count(boneNames.Contains);
        if (missingRequired.Length > 0 || limbAnchorCount < 2)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "family.humanoid-anchors-insufficient",
                Dl1ClassificationEvidenceSource.DecodedRig,
                Dl1ClassificationConfidence.High,
                $"The rig is missing {missingRequired.Length} required anchor(s) and has {limbAnchorCount} of {HumanoidLimbAnchors.Length} bilateral limb anchors; the name hint was not promoted."));
            return (Dl1RigFamily.Unknown, Dl1ClassificationConfidence.None);
        }

        Dl1ClassificationConfidence confidence =
            limbAnchorCount == HumanoidLimbAnchors.Length
                ? Dl1ClassificationConfidence.High
                : Dl1ClassificationConfidence.Medium;
        evidence.Add(new Dl1ClassificationEvidence(
            "family.humanoid-anchors-corroborated",
            Dl1ClassificationEvidenceSource.DecodedRig,
            confidence,
            $"Decoded skin plus the root, pelvis, head, and {limbAnchorCount} bilateral limb anchors corroborate the {hintedFamily} name hint."));
        return (hintedFamily, confidence);
    }

    private static (
        Dl1MeshPerspective Perspective,
        Dl1ClassificationConfidence Confidence)
        ClassifyPerspective(
            string resourceName,
            List<Dl1ClassificationEvidence> evidence)
    {
        HashSet<string> tokens = Tokenize(resourceName);
        bool fpp = tokens.Contains("fpp");
        bool tpp = tokens.Contains("tpp");
        if (fpp == tpp)
        {
            if (fpp)
            {
                evidence.Add(new Dl1ClassificationEvidence(
                    "perspective.conflicting-tokens",
                    Dl1ClassificationEvidenceSource.ResourceIdentity,
                    Dl1ClassificationConfidence.High,
                    "Both FPP and TPP tokens are present, so perspective remains unknown."));
            }

            return (
                Dl1MeshPerspective.Unknown,
                Dl1ClassificationConfidence.None);
        }

        Dl1MeshPerspective perspective = fpp
            ? Dl1MeshPerspective.FirstPerson
            : Dl1MeshPerspective.ThirdPerson;
        evidence.Add(new Dl1ClassificationEvidence(
            fpp
                ? "perspective.explicit-fpp-token"
                : "perspective.explicit-tpp-token",
            Dl1ClassificationEvidenceSource.ResourceIdentity,
            Dl1ClassificationConfidence.High,
            $"The resource name contains an explicit {(fpp ? "FPP" : "TPP")} token."));
        return (perspective, Dl1ClassificationConfidence.High);
    }

    private static Dl1FacialSupport ClassifyFacialSupport(
        Dl1MeshData mesh,
        List<Dl1ClassificationEvidence> evidence)
    {
        int decoded = mesh.MorphTargets.Count(static target =>
            target.PayloadStatus ==
                Dl1MorphPayloadStatus.VertexDeltasDecoded);
        if (decoded > 0)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "facial.decoded-morph-deltas",
                Dl1ClassificationEvidenceSource.DecodedMorphs,
                Dl1ClassificationConfidence.High,
                $"Decoded vertex deltas for {decoded} of {mesh.MorphTargets.Count} morph target(s)."));
            return Dl1FacialSupport.DecodedMorphDeltas;
        }

        if (mesh.MorphTargets.Count > 0)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "facial.morph-inventory",
                Dl1ClassificationEvidenceSource.DecodedMorphs,
                Dl1ClassificationConfidence.Medium,
                $"Decoded {mesh.MorphTargets.Count} morph channel name(s), but no vertex-delta payload."));
            return Dl1FacialSupport.MorphChannels;
        }

        if (mesh.IsStructurallyValid && mesh.HasDecodedGeometry)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "facial.no-morph-inventory",
                Dl1ClassificationEvidenceSource.DecodedMorphs,
                Dl1ClassificationConfidence.High,
                "The decoded mesh has no morph channel inventory."));
            return Dl1FacialSupport.None;
        }

        evidence.Add(new Dl1ClassificationEvidence(
            "facial.not-decoded",
            Dl1ClassificationEvidenceSource.DecodedMorphs,
            Dl1ClassificationConfidence.None,
            "The resource does not provide enough decoded geometry evidence to classify facial support."));
        return Dl1FacialSupport.Unknown;
    }

    private static (
        Dl1RetailSourceScope Scope,
        string? DlcIdentifier)
        ClassifySource(
            RetailAssetSource source,
            List<Dl1ClassificationEvidence> evidence)
    {
        if (source.Kind == RetailAssetSourceKind.GeneratedOverride ||
            source.Priority >=
                Dl1RetailProviderSet.FirstAdditionalRpackPriority)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "source.user-added",
                Dl1ClassificationEvidenceSource.RetailProvider,
                Dl1ClassificationConfidence.High,
                "Provider kind or configured precedence identifies a user-added source."));
            return (Dl1RetailSourceScope.UserAdded, null);
        }

        string[] segments = Path.GetFullPath(source.ContainerPath)
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        string? dlc = segments.FirstOrDefault(static segment =>
            DlcDirectoryPattern().IsMatch(segment));
        if (dlc is not null)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "source.dlc-directory",
                Dl1ClassificationEvidenceSource.RetailProvider,
                Dl1ClassificationConfidence.High,
                $"Container path is under the '{dlc}' retail DLC root."));
            return (Dl1RetailSourceScope.Dlc, dlc.ToUpperInvariant());
        }

        bool baseGame = segments.Any(static segment =>
            segment.Equals("DW", StringComparison.OrdinalIgnoreCase));
        if (baseGame)
        {
            evidence.Add(new Dl1ClassificationEvidence(
                "source.base-directory",
                Dl1ClassificationEvidenceSource.RetailProvider,
                Dl1ClassificationConfidence.High,
                "Container path is under the base retail DW root."));
            return (Dl1RetailSourceScope.BaseGame, null);
        }

        evidence.Add(new Dl1ClassificationEvidence(
            "source.unclassified",
            Dl1ClassificationEvidenceSource.RetailProvider,
            Dl1ClassificationConfidence.None,
            "Provider metadata does not prove base, DLC, or user-added scope."));
        return (Dl1RetailSourceScope.Unknown, null);
    }

    private static Dl1RigFamily GetFamilyNameHint(string resourceName)
    {
        string normalized = NormalizeResourceName(resourceName);
        HashSet<string> tokens = Tokenize(normalized);
        if (tokens.Contains("player"))
        {
            return Dl1RigFamily.Player;
        }

        if (tokens.Contains("volatile") ||
            tokens.Contains("voleteile"))
        {
            return Dl1RigFamily.Volatile;
        }

        if (tokens.Contains("screamer"))
        {
            return Dl1RigFamily.Screamer;
        }

        if (tokens.Contains("demolisher") ||
            normalized is "armored" or "armored_b" or "armored_rock")
        {
            return Dl1RigFamily.Demolisher;
        }

        if (tokens.Contains("goon"))
        {
            return Dl1RigFamily.Goon;
        }

        if (tokens.Contains("infected") ||
            normalized is
                "zombie_man_a" or
                "zombie_woman" or
                "zombie_prime")
        {
            return Dl1RigFamily.GenericInfected;
        }

        if (tokens.Contains("npc") ||
            normalized is
                "jade" or
                "rais" or
                "survivor_a" or
                "survivor_woman_a")
        {
            return Dl1RigFamily.GenericNpc;
        }

        return Dl1RigFamily.Unknown;
    }

    private static string NormalizeResourceName(string value) =>
        value
            .Trim()
            .Replace('\\', '/')
            .TrimStart('/')
            .ToLowerInvariant();

    private static HashSet<string> Tokenize(string value) =>
        NonAlphaNumericPattern()
            .Split(NormalizeResourceName(value))
            .Where(static token => token.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string ToStableToken(Dl1RigFamily family) =>
        family.ToString().ToLowerInvariant();

    [GeneratedRegex(@"^DW_DLC[0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex DlcDirectoryPattern();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex NonAlphaNumericPattern();
}

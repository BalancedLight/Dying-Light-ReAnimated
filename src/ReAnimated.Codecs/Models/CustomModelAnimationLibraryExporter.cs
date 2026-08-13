using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;

namespace ReAnimated.Codecs.Models;

public sealed record CustomModelAnimationLibraryRequest
{
    public required FbxModelAuthoringImportResult Model { get; init; }

    public required string OutputPath { get; init; }

    /// <summary>
    /// Optional edited stack metadata. When omitted, the immutable imported
    /// selections in the .dlrmodel document are used.
    /// </summary>
    public ImmutableArray<CustomModelAnimationClip> Selections { get; init; } = [];
}

public sealed record CustomModelAnimationLibraryResult(
    string OutputPath,
    string ManifestPath,
    string OutputSha256,
    ImmutableArray<string> AnimationNames,
    ImmutableArray<string> Warnings);

/// <summary>
/// Exports every selected FBX animation stack through the same authoritative
/// DL1 evaluation/ANM2 path used by the editor, then builds one deterministic
/// animation-library RPack.
/// </summary>
public static class CustomModelAnimationLibraryExporter
{
    private static readonly string[] ManifestLimitations =
    [
        "No animation-script resource was synthesized.",
        "Generated auxiliary 0xCCC3CDDF tracks remain blocked until their writer passes the installed DL1 validation corpus.",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<CustomModelAnimationLibraryResult> ExportAsync(
        CustomModelAnimationLibraryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        request.Model.Package.Document.Validate();
        if (request.Model.Rig is null || request.Model.Package.Document.Bones.IsEmpty)
        {
            throw new InvalidOperationException("A static custom model has no rig for animation-library export.");
        }

        ImmutableArray<CustomModelAnimationClip> selections = request.Selections.IsDefaultOrEmpty
            ? request.Model.Package.Document.AnimationClips
            : request.Selections;
        CustomModelAnimationClip[] included = selections.Where(static clip => clip.Included).ToArray();
        if (included.Length == 0)
        {
            throw new InvalidOperationException("Select at least one decoded FBX animation stack for RPack export.");
        }

        Guid[] duplicateIds = included.GroupBy(static clip => clip.Id)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            throw new InvalidDataException("Animation stack selections contain duplicate identities.");
        }

        RigDefinition rig = BuildDescriptorRig(request.Model.Package.Document, request.Model.Rig);
        ImmutableArray<uint> descriptorOrder = rig.Bones
            .Select(static bone => bone.DescriptorHash!.Value)
            .ToImmutableArray();
        var animations = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var warnings = ImmutableArray.CreateBuilder<string>();
        var rows = new List<object>();
        var exporter = new Dl1AnimationExporter(new Anm2EvaluationAdapter(new AnimationEvaluator()));
        foreach (CustomModelAnimationClip selection in included)
        {
            cancellationToken.ThrowIfCancellationRequested();
            selection.ValidateForExport();
            if (!request.Model.AnimationClips.TryGetValue(selection.Id, out AnimationClip? imported))
            {
                throw new InvalidOperationException(
                    $"Animation stack '{selection.DisplayName}' is retained as metadata but has no decoded clip. Reimport the FBX after resolving its stack diagnostics.");
            }

            if (imported.FrameCount > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Animation stack '{selection.DisplayName}' has {imported.FrameCount:N0} frames; DL1 ANM2 permits at most {ushort.MaxValue:N0}.");
            }

            if (selection.RootMotionMode == Dl1RootMotionMode.MotionAccumulator)
            {
                throw new InvalidOperationException(
                    $"Animation stack '{selection.DisplayName}' requests generated 0xCCC3CDDF accumulator output. The current verified ANM2 exporter preserves recorded, InPlace, and Bip01 policies; it will not fabricate an unverified auxiliary accumulator track.");
            }

            string name = Dl1SourceModelWriter.SanitizeName(selection.DisplayName, 63);
            if (animations.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"Selected animation names collide after DL1 resource-name normalization: '{name}'. Rename one stack in the Models workspace.");
            }

            AnimationClip clip = Reframe(imported, name, selection.FrameRate);
            AnimationRootMode rootMode = selection.RootMotionMode switch
            {
                Dl1RootMotionMode.Recorded => AnimationRootMode.Recorded,
                Dl1RootMotionMode.InPlace => AnimationRootMode.InPlace,
                Dl1RootMotionMode.Bip01 => AnimationRootMode.Bip01,
                _ => throw new InvalidOperationException($"Unsupported root policy '{selection.RootMotionMode}'."),
            };
            Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
                rig,
                rig,
                null,
                rootMode,
                selection.RootBoneName);
            var evaluation = new EvaluationRequest(
                rig,
                rig,
                clip,
                0,
                PreviewProfile.RawAuthoring,
                purpose: EvaluationPurpose.Export,
                dl1AuthoringPolicy: policy);
            Dl1AnimationExportResult result = exporter.Export(
                new Dl1AnimationExportRequest
                {
                    Evaluation = evaluation,
                    Parts = Dl1AnimationExportParts.Body,
                    BodyDescriptorOrder = descriptorOrder,
                },
                cancellationToken);
            byte[] payload = result.BodyAnm2 ?? throw new InvalidOperationException(
                $"Animation stack '{selection.DisplayName}' produced no body ANM2 payload.");
            animations.Add(name, payload);
            rows.Add(new
            {
                name,
                selection.SourceName,
                selection.SourceFingerprint,
                selection.FrameRate.Numerator,
                selection.FrameRate.Denominator,
                frameCount = clip.FrameCount,
                rootPolicy = selection.RootMotionMode.ToString(),
                selection.RootBoneName,
                sha256 = Sha256(payload),
            });
        }

        byte[] rpack = Rp6lAnimationLibraryCodec.Build(
            animations,
            new Dictionary<string, Rp6lAnimationScript>());
        string outputPath = Path.GetFullPath(request.OutputPath);
        if (!string.Equals(Path.GetExtension(outputPath), ".rpack", StringComparison.OrdinalIgnoreCase))
        {
            outputPath += ".rpack";
        }

        await Rp6lAnimationLibraryCodec.WriteAtomicAsync(outputPath, rpack, cancellationToken).ConfigureAwait(false);
        string manifestPath = Path.ChangeExtension(outputPath, ".animations.json");
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "dl-reanimated-csharp-custom-animation-library",
            schemaVersion = 1,
            modelId = request.Model.Package.Document.ModelId,
            sourceFbxSha256 = request.Model.Package.Document.Source.ContentSha256,
            rigSignature = request.Model.Package.Document.RigSignature,
            outputSha256 = Sha256(rpack),
            animations = rows,
            limitations = ManifestLimitations,
        }, JsonOptions);
        await Rp6lAnimationLibraryCodec.WriteAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        warnings.Add("The RPack contains animation resources only; attach them through an explicit, reviewed DL1 animation script.");
        return new CustomModelAnimationLibraryResult(
            outputPath,
            manifestPath,
            Sha256(rpack),
            animations.Keys.Order(StringComparer.Ordinal).ToImmutableArray(),
            warnings.ToImmutable());
    }

    private static RigDefinition BuildDescriptorRig(CustomModelDocument document, RigDefinition importedRig)
    {
        var seen = new Dictionary<uint, string>();
        var bones = ImmutableArray.CreateBuilder<BoneDefinition>(importedRig.BoneCount);
        foreach (BoneDefinition bone in importedRig.Bones)
        {
            uint descriptor = Dl1NameHash.Compute(bone.Name);
            if (seen.TryGetValue(descriptor, out string? existing))
            {
                throw new InvalidDataException(
                    $"DL1 descriptor collision 0x{descriptor:X8} between custom-rig entities '{existing}' and '{bone.Name}'. Rename one entity before export.");
            }

            seen.Add(descriptor, bone.Name);
            bones.Add(new BoneDefinition(
                bone.Index,
                bone.Name,
                bone.ParentIndex,
                bone.LocalBindPose,
                bone.Kind,
                requiredForExport: true,
                descriptorHash: descriptor,
                semanticRole: bone.SemanticRole));
        }

        return new RigDefinition(
            $"{importedRig.Id}:dl1-descriptors",
            importedRig.DisplayName,
            bones.MoveToImmutable(),
            importedRig.MorphChannels,
            importedRig.SourceAssetFingerprint,
            importedRig.IkChains);
    }

    private static AnimationClip Reframe(AnimationClip source, string name, FrameRate frameRate) =>
        new(
            name,
            frameRate,
            source.FrameCount,
            source.TransformTracks,
            source.ScalarTracks,
            source.AuxiliaryTransformTracks);

    private static string Sha256(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static void ValidateForExport(this CustomModelAnimationClip selection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.DisplayName);
        if (selection.FrameCount <= 0 || selection.StartFrame < 0)
        {
            throw new InvalidDataException($"Animation stack '{selection.DisplayName}' has an invalid range.");
        }

        if (selection.RootBoneName is null)
        {
            throw new InvalidOperationException($"Select an explicit root bone for animation stack '{selection.DisplayName}'.");
        }
    }
}

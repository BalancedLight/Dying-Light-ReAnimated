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
    /// Extensionless name of the type-322 AnimationScr resource referenced by
    /// the model's AnimScriptAlias virtual <c>.scr</c> filename. When omitted,
    /// the authored model setting is used.
    /// </summary>
    public string? AnimationScriptAlias { get; init; }

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
    string AnimationScriptName,
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

        string? requestedAlias = string.IsNullOrWhiteSpace(request.AnimationScriptAlias)
            ? request.Model.Package.Document.BuildSettings.AnimationScriptAlias
            : request.AnimationScriptAlias;
        if (string.IsNullOrWhiteSpace(requestedAlias))
        {
            throw new InvalidOperationException(
                "Selected animation stacks require an animation script identity so the model .ascr can resolve the type-322 RPack resource.");
        }

        string scriptName = Dl1SourceModelWriter.RequireExactResourceName(
            requestedAlias,
            63,
            "animation script alias");

        Guid[] duplicateIds = included.GroupBy(static clip => clip.Id)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            throw new InvalidDataException("Animation stack selections contain duplicate identities.");
        }

        Dl1PreparedAuthoredRig preparedRig = Dl1CustomModelRigPreparer.Prepare(
            request.Model,
            cancellationToken);
        RigDefinition rig = preparedRig.PreviewRig;
        ImmutableArray<uint> descriptorOrder = rig.Bones
            .Select(static bone => bone.DescriptorHash!.Value)
            .ToImmutableArray();
        var animations = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var scriptSequences = new List<AnimationScrSequence>(included.Length);
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

            AnimationClip clip = RebaseToAuthoredRig(
                imported,
                preparedRig,
                name,
                selection.FrameRate,
                cancellationToken);
            if (clip.FrameCount > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Animation stack '{selection.DisplayName}' resamples to {clip.FrameCount:N0} frames at " +
                    $"{selection.FrameRate.Numerator}/{selection.FrameRate.Denominator} FPS; DL1 ANM2 permits at most {ushort.MaxValue:N0}.");
            }
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
            float framesPerSecond = checked((float)selection.FrameRate.FramesPerSecond);
            if (!float.IsFinite(framesPerSecond) || framesPerSecond <= 0.0f)
            {
                throw new InvalidOperationException(
                    $"Animation stack '{selection.DisplayName}' has a frame rate that cannot be represented by DL1 AnimationScr.");
            }

            scriptSequences.Add(new AnimationScrSequence(
                name,
                $"{name}.anm2",
                0.0f,
                clip.FrameCount - 1,
                framesPerSecond,
                Enabled: 1,
                Blend: 0.5f));
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

        if (animations.ContainsKey(scriptName))
        {
            throw new InvalidOperationException(
                $"Animation script alias '{scriptName}' collides with an exported animation resource name. Choose a distinct alias.");
        }

        AnimationScrSections scriptSections = AnimationScrCodec.Build(scriptSequences);
        var scripts = new Dictionary<string, Rp6lAnimationScript>(StringComparer.OrdinalIgnoreCase)
        {
            [scriptName] = new Rp6lAnimationScript(
                scriptSections.RecordsAndNames,
                scriptSections.IndexAndNames),
        };

        byte[] rpack = Rp6lAnimationLibraryCodec.Build(
            animations,
            scripts);
        string outputPath = Path.GetFullPath(request.OutputPath);
        if (!string.Equals(Path.GetExtension(outputPath), ".rpack", StringComparison.OrdinalIgnoreCase))
        {
            outputPath += ".rpack";
        }

        await WriteValidatedRpackAtomicAsync(
            outputPath,
            rpack,
            animations,
            scriptName,
            scriptSequences,
            cancellationToken).ConfigureAwait(false);
        string manifestPath = Path.ChangeExtension(outputPath, ".animations.json");
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "dl-reanimated-csharp-custom-animation-library",
            schemaVersion = 1,
            modelId = request.Model.Package.Document.ModelId,
            sourceFbxSha256 = request.Model.Package.Document.Source.ContentSha256,
            sourceRigSignature = request.Model.Package.Document.RigSignature,
            authoredRigContract = new
            {
                preparedRig.Contract.ContractId,
                preparedRig.Contract.BindFingerprint,
                preparedRig.Contract.SkeletonFingerprint,
                preparedRig.Contract.DescriptorFingerprint,
            },
            outputSha256 = Sha256(rpack),
            animationScript = new
            {
                name = scriptName,
                resourceType = Rp6lResourceTypes.AnimationScript,
                sequenceCount = scriptSequences.Count,
                sequences = scriptSequences.Select(static sequence => new
                {
                    sequence.Name,
                    sequence.Anm2Name,
                    sequence.StartFrame,
                    sequence.EndFrame,
                    sequence.FramesPerSecond,
                    sequence.Enabled,
                    sequence.Blend,
                }),
            },
            animations = rows,
            limitations = ManifestLimitations,
        }, JsonOptions);
        await Rp6lAnimationLibraryCodec.WriteAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        warnings.Add(
            $"The RPack contains {animations.Count:N0} animation resource(s) plus extensionless type-322 script '{scriptName}'. The model .ascr resolves it as '{Dl1SourceModelWriter.AnimationScriptFileName(scriptName)}'.");
        return new CustomModelAnimationLibraryResult(
            outputPath,
            manifestPath,
            Sha256(rpack),
            scriptName,
            animations.Keys.Order(StringComparer.Ordinal).ToImmutableArray(),
            warnings.ToImmutable());
    }

    private static async Task WriteValidatedRpackAtomicAsync(
        string outputPath,
        byte[] payload,
        IReadOnlyDictionary<string, byte[]> expectedAnimations,
        string expectedScriptName,
        IReadOnlyList<AnimationScrSequence> expectedSequences,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Animation RPack output path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string stagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.validate.tmp");
        try
        {
            await Rp6lAnimationLibraryCodec.WriteAtomicAsync(
                stagingPath,
                payload,
                cancellationToken).ConfigureAwait(false);
            Rp6lAnimationLibrary reopened = await Rp6lAnimationLibraryCodec.ExtractAsync(
                stagingPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            ValidateReopenedLibrary(
                reopened,
                expectedAnimations,
                expectedScriptName,
                expectedSequences);
            File.Move(stagingPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private static void ValidateReopenedLibrary(
        Rp6lAnimationLibrary reopened,
        IReadOnlyDictionary<string, byte[]> expectedAnimations,
        string expectedScriptName,
        IReadOnlyList<AnimationScrSequence> expectedSequences)
    {
        if (reopened.Animations.Count != expectedAnimations.Count ||
            reopened.AnimationScripts.Count != 1 ||
            !reopened.AnimationScripts.TryGetValue(
                expectedScriptName,
                out Rp6lAnimationScript? script))
        {
            throw new InvalidDataException(
                "Reopened animation RPack does not contain the staged animation/script resource set.");
        }

        foreach ((string name, byte[] expectedPayload) in expectedAnimations)
        {
            if (!reopened.Animations.TryGetValue(name, out byte[]? actualPayload) ||
                !expectedPayload.AsSpan().SequenceEqual(actualPayload))
            {
                throw new InvalidDataException(
                    $"Reopened animation RPack resource '{name}' differs from the staged ANM2 payload.");
            }
        }

        ParsedAnimationScr parsed = AnimationScrCodec.Parse(
            new AnimationScrSections(script.HeaderSection, script.BodySection));
        ParsedAnimationScrSequence[] actualSequences = parsed.Sequences
            .OrderBy(static sequence => sequence.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AnimationScrSequence[] orderedExpected = expectedSequences
            .OrderBy(static sequence => sequence.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (parsed.DeclaredSequenceCount != orderedExpected.Length ||
            actualSequences.Length != orderedExpected.Length)
        {
            throw new InvalidDataException(
                "Reopened animation RPack script sequence count differs from the staged selection.");
        }

        for (var index = 0; index < orderedExpected.Length; index++)
        {
            AnimationScrSequence expected = orderedExpected[index];
            ParsedAnimationScrSequence actual = actualSequences[index];
            if (!string.Equals(expected.Name, actual.Name, StringComparison.OrdinalIgnoreCase) ||
                actual.StartFrame != expected.StartFrame ||
                actual.EndFrame != expected.EndFrame ||
                actual.FramesPerSecond != expected.FramesPerSecond ||
                actual.Enabled != expected.Enabled ||
                actual.Blend != expected.Blend ||
                actual.EventCount != 0)
            {
                throw new InvalidDataException(
                    $"Reopened animation RPack script sequence '{expected.Name}' differs from the staged timing contract.");
            }
        }
    }

    private static AnimationClip RebaseToAuthoredRig(
        AnimationClip source,
        Dl1PreparedAuthoredRig prepared,
        string name,
        FrameRate frameRate,
        CancellationToken cancellationToken)
    {
        long outputFrameCount = source.FrameCount == 1
            ? 1
            : Math.Max(
                2,
                checked((long)Math.Round(
                    source.DurationSeconds * frameRate.FramesPerSecond,
                    MidpointRounding.AwayFromZero) + 1));
        if (outputFrameCount <= 0 || outputFrameCount > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"Animation '{name}' resamples to {outputFrameCount:N0} frames; DL1 ANM2 permits between 1 and {ushort.MaxValue:N0} frames.");
        }

        var keysByBone = Enumerable.Range(0, prepared.PreviewRig.BoneCount)
            .Select(_ => ImmutableArray.CreateBuilder<TransformKeyframe>(checked((int)outputFrameCount)))
            .ToArray();
        for (long frame = 0; frame < outputFrameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double seconds = Math.Min(
                source.DurationSeconds,
                frameRate.SecondsForFrame(frame));
            SkeletonPose sourcePose = source.SamplePose(
                prepared.SourceRig,
                seconds,
                PlaybackMode.Clamp);
            SkeletonPose rebased = prepared.RebasePose(sourcePose);
            for (int boneIndex = 0; boneIndex < rebased.LocalTransforms.Length; boneIndex++)
            {
                keysByBone[boneIndex].Add(new TransformKeyframe(frame, rebased.LocalTransforms[boneIndex]));
            }
        }

        ImmutableArray<TransformTrack> tracks = keysByBone
            .Select((keys, index) => new TransformTrack(index, keys.MoveToImmutable()))
            .ToImmutableArray();
        ImmutableArray<ScalarTrack> scalarTracks = source.ScalarTracks
            .Select(track => new ScalarTrack(
                track.ChannelName,
                Enumerable.Range(0, checked((int)outputFrameCount))
                    .Select(frame => new ScalarKeyframe(
                        frame,
                        track.Sample(source.ResolveFrame(
                            Math.Min(source.DurationSeconds, frameRate.SecondsForFrame(frame)),
                            PlaybackMode.Clamp))))))
            .ToImmutableArray();
        ImmutableArray<AuxiliaryTransformTrack> auxiliaryTracks = source.AuxiliaryTransformTracks
            .Select(track => new AuxiliaryTransformTrack(
                track.Descriptor,
                Enumerable.Range(0, checked((int)outputFrameCount))
                    .Select(frame => new TransformKeyframe(
                        frame,
                        track.Sample(source.ResolveFrame(
                            Math.Min(source.DurationSeconds, frameRate.SecondsForFrame(frame)),
                            PlaybackMode.Clamp))))))
            .ToImmutableArray();
        return new AnimationClip(
            name,
            frameRate,
            outputFrameCount,
            tracks,
            scalarTracks,
            auxiliaryTracks);
    }

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

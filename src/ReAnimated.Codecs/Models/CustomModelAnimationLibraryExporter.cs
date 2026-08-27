using System.Collections.Immutable;
using System.Globalization;
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

public sealed record PreparedCustomModelAnimation(
    string Name,
    string Anm2FileName,
    byte[] Payload,
    int FrameCount,
    float FramesPerSecond,
    string SourceName,
    string SourceFingerprint,
    Dl1RootMotionMode RootMotionMode,
    string? RootBoneName);

public sealed record PreparedCustomModelAnimationLibrary(
    string AnimationScriptName,
    ImmutableArray<PreparedCustomModelAnimation> Animations,
    ImmutableArray<AnimationScrSequence> Sequences,
    string LooseScriptText,
    ImmutableArray<string> Warnings)
{
    /// <summary>
    /// True when <see cref="LooseScriptText"/> was hand-authored rather than
    /// generated. Authored text cannot match the generator byte for byte, so
    /// the staging checks verify the sequence inventory it declares instead of
    /// comparing it against a regenerated script.
    /// </summary>
    public bool IsAuthoredScript { get; init; }

    public byte[] BuildPortableRpack()
    {
        AnimationScrSections sections = AnimationScrCodec.Build(Sequences);
        return Rp6lAnimationLibraryCodec.Build(
            Animations.ToDictionary(
                static animation => animation.Name,
                static animation => animation.Payload,
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Rp6lAnimationScript>(StringComparer.OrdinalIgnoreCase)
            {
                [AnimationScriptName] = new Rp6lAnimationScript(
                    sections.RecordsAndNames,
                    sections.IndexAndNames),
            });
    }
}

/// <summary>
/// Exports every selected FBX animation stack through the same authoritative
/// DL1 evaluation/ANM2 path used by the editor, then builds one deterministic
/// animation-library RPack.
/// </summary>
public static class CustomModelAnimationLibraryExporter
{
    /// <summary>
    /// Verifies that a prepared library's loose script really covers its
    /// sequence inventory.
    /// </summary>
    /// <remarks>
    /// A generated script is checked by regenerating it and comparing bytes.
    /// An authored one cannot be, so every declared sequence is matched
    /// against a SeqTrack row instead. Skipping the check entirely would let a
    /// hand edit silently drop a sequence and ship a deployment whose script
    /// does not mention an animation it packaged.
    /// </remarks>
    public static void ValidateLooseScriptCoversInventory(
        PreparedCustomModelAnimationLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (!library.IsAuthoredScript)
        {
            string expected = BuildLooseAnimationScript(library.Sequences);
            if (!string.Equals(
                    library.LooseScriptText,
                    expected,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The animation SCR differs from its prepared sequence inventory.");
            }

            return;
        }

        ImmutableArray<AnimationScriptSeqTrack> authored =
            AnimationScriptSourceParser.ParseSeqTracks(library.LooseScriptText);
        foreach (AnimationScrSequence sequence in library.Sequences)
        {
            AnimationScriptSeqTrack[] matches =
            [
                .. authored.Where(track => string.Equals(
                    track.Name,
                    sequence.Name,
                    StringComparison.OrdinalIgnoreCase)),
            ];
            if (matches.Length != 1)
            {
                throw new InvalidDataException(
                    $"The authored animation SCR declares {matches.Length} SeqTrack rows named '{sequence.Name}'; exactly one is required.");
            }

            AnimationScriptSeqTrack track = matches[0];
            if (!string.Equals(
                    track.Anm2Name,
                    sequence.Anm2Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The authored SeqTrack '{sequence.Name}' names '{track.Anm2Name}' instead of '{sequence.Anm2Name}'.");
            }

            // A symbolic field comes from a .def include this tool does not
            // resolve, so it is accepted as authored intent; a literal that
            // disagrees with the packaged animation is a real mismatch.
            EnsureTimingMatches(
                sequence.Name,
                "start frame",
                track.StartFrame,
                sequence.StartFrame);
            EnsureTimingMatches(
                sequence.Name,
                "end frame",
                track.EndFrame,
                sequence.EndFrame);
            EnsureTimingMatches(
                sequence.Name,
                "frame rate",
                track.FramesPerSecond,
                sequence.FramesPerSecond);
        }
    }

    private static void EnsureTimingMatches(
        string sequenceName,
        string description,
        AnimationScriptValue authored,
        float expected)
    {
        if (authored.Number is { } value &&
            Math.Abs(value - expected) > 0.001f)
        {
            throw new InvalidDataException(
                $"The authored SeqTrack '{sequenceName}' declares a {description} of {authored.Text} instead of {expected.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        }
    }

    private static readonly string[] ManifestLimitations =
    [
        "Generated auxiliary 0xCCC3CDDF tracks remain blocked until their writer passes the installed DL1 validation corpus.",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<PreparedCustomModelAnimationLibrary> PrepareAsync(
        CustomModelAnimationLibraryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Model);
        request.Model.Package.Document.Validate();
        if (!request.Model.Package.Document.Bones.IsEmpty)
        {
            _ = request.Model.Package.Document
                .CreateDl1AnimationRigDefinition();
        }
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
            throw new InvalidOperationException("Select at least one decoded FBX animation stack for export.");
        }

        string? requestedAlias = string.IsNullOrWhiteSpace(request.AnimationScriptAlias)
            ? request.Model.Package.Document.BuildSettings.AnimationScriptAlias
            : request.AnimationScriptAlias;
        if (string.IsNullOrWhiteSpace(requestedAlias))
        {
            throw new InvalidOperationException(
                "Selected animation stacks require an animation library/script name so the model ASCR and loose SCR resolve the same identity.");
        }

        string scriptName = Dl1SourceModelWriter.RequireExactResourceName(
            requestedAlias,
            63,
            "animation library/script name");
        if (included.GroupBy(static clip => clip.Id).Any(static group => group.Count() > 1))
        {
            throw new InvalidDataException("Animation stack selections contain duplicate identities.");
        }

        Dl1PreparedAuthoredRig preparedRig = Dl1CustomModelRigPreparer.Prepare(request.Model, cancellationToken);
        RigDefinition rig = preparedRig.PreviewRig;
        ImmutableArray<uint> descriptorOrder = rig.Bones
            .Select(static bone => bone.DescriptorHash!.Value)
            .ToImmutableArray();
        var preparedAnimations = ImmutableArray.CreateBuilder<PreparedCustomModelAnimation>(included.Length);
        var sequences = ImmutableArray.CreateBuilder<AnimationScrSequence>(included.Length);
        var warnings = ImmutableArray.CreateBuilder<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    $"Animation stack '{selection.DisplayName}' requests generated 0xCCC3CDDF accumulator output. The verified writer does not fabricate that auxiliary track.");
            }

            string name = Dl1SourceModelWriter.SanitizeName(selection.DisplayName, 63);
            if (!names.Add(name))
            {
                throw new InvalidOperationException(
                    $"Selected animation names collide after DL1 resource-name normalization: '{name}'. Rename one stack in the Models workspace.");
            }

            int integerFramesPerSecond = checked((int)Math.Round(
                selection.FrameRate.FramesPerSecond,
                MidpointRounding.AwayFromZero));
            if (integerFramesPerSecond is < 1 or > 240)
            {
                throw new InvalidOperationException(
                    $"Animation stack '{selection.DisplayName}' resolves to unsupported integer cadence {integerFramesPerSecond} FPS.");
            }

            var outputFrameRate = new FrameRate(integerFramesPerSecond, 1);
            if (selection.FrameRate != outputFrameRate)
            {
                warnings.Add(
                    $"Animation stack '{selection.DisplayName}' was deterministically resampled from " +
                    $"{selection.FrameRate.Numerator}/{selection.FrameRate.Denominator} FPS to {integerFramesPerSecond} FPS for matching ANM2 and SCR timing.");
            }

            AnimationClip clip = RebaseToAuthoredRig(
                imported,
                preparedRig,
                name,
                outputFrameRate,
                cancellationToken);
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
            string anm2FileName = $"{name}.anm2";
            preparedAnimations.Add(new PreparedCustomModelAnimation(
                name,
                anm2FileName,
                payload,
                checked((int)clip.FrameCount),
                integerFramesPerSecond,
                selection.SourceName,
                selection.SourceFingerprint,
                selection.RootMotionMode,
                selection.RootBoneName));
            sequences.Add(new AnimationScrSequence(
                name,
                anm2FileName,
                0.0f,
                clip.FrameCount - 1,
                integerFramesPerSecond,
                Enabled: 1,
                Blend: 0.5f));
        }

        if (names.Contains(scriptName))
        {
            throw new InvalidOperationException(
                $"Animation library/script name '{scriptName}' collides with an exported animation resource name.");
        }

        ImmutableArray<AnimationScrSequence> sequenceRows = sequences.MoveToImmutable();
        return new PreparedCustomModelAnimationLibrary(
            scriptName,
            preparedAnimations.MoveToImmutable(),
            sequenceRows,
            BuildLooseAnimationScript(sequenceRows),
            warnings.ToImmutable());
    }

    public static string BuildLooseAnimationScript(IEnumerable<AnimationScrSequence> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        AnimationScrSequence[] ordered = sequences
            .OrderBy(static sequence => sequence.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _ = AnimationScrCodec.Build(ordered);
        var builder = new StringBuilder("// Generated by DL ReAnimated. Source timing matches compiled ANM2 output.\n");
        foreach (AnimationScrSequence sequence in ordered)
        {
            builder.Append("SeqTrack( \"")
                .Append(sequence.Name)
                .Append("\", \"")
                .Append(sequence.Anm2Name)
                .Append("\", ")
                .Append(sequence.StartFrame.ToString("0", CultureInfo.InvariantCulture))
                .Append(", ")
                .Append(sequence.EndFrame.ToString("0", CultureInfo.InvariantCulture))
                .Append(", ")
                .Append(sequence.FramesPerSecond.ToString("0", CultureInfo.InvariantCulture))
                .Append(", ")
                .Append(sequence.Enabled.ToString(CultureInfo.InvariantCulture))
                .Append(", ")
                .Append(sequence.Blend.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" )\n");
        }

        return builder.ToString();
    }

    public static async Task<CustomModelAnimationLibraryResult> ExportAsync(
        CustomModelAnimationLibraryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        PreparedCustomModelAnimationLibrary prepared = await PrepareAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        byte[] rpack = prepared.BuildPortableRpack();
        Dictionary<string, byte[]> animations = prepared.Animations.ToDictionary(
            static animation => animation.Name,
            static animation => animation.Payload,
            StringComparer.OrdinalIgnoreCase);
        string outputPath = Path.GetFullPath(request.OutputPath);
        if (!string.Equals(Path.GetExtension(outputPath), ".rpack", StringComparison.OrdinalIgnoreCase))
        {
            outputPath += ".rpack";
        }

        await WriteValidatedRpackAtomicAsync(
            outputPath,
            rpack,
            animations,
            prepared.AnimationScriptName,
            prepared.Sequences,
            cancellationToken).ConfigureAwait(false);
        string manifestPath = Path.ChangeExtension(outputPath, ".animations.json");
        Dl1PreparedAuthoredRig preparedRig = Dl1CustomModelRigPreparer.Prepare(
            request.Model,
            cancellationToken);
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
                name = prepared.AnimationScriptName,
                resourceType = Rp6lResourceTypes.AnimationScript,
                sequenceCount = prepared.Sequences.Length,
                sequences = prepared.Sequences.Select(static sequence => new
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
            animations = prepared.Animations.Select(static animation => new
            {
                animation.Name,
                animation.Anm2FileName,
                animation.SourceName,
                animation.SourceFingerprint,
                frameCount = animation.FrameCount,
                framesPerSecond = animation.FramesPerSecond,
                rootPolicy = animation.RootMotionMode.ToString(),
                animation.RootBoneName,
                sha256 = Sha256(animation.Payload),
            }),
            limitations = ManifestLimitations,
        }, JsonOptions);
        await Rp6lAnimationLibraryCodec.WriteAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        var warnings = prepared.Warnings.ToBuilder();
        warnings.Add(
            $"The portable RPack contains {animations.Count:N0} animation resource(s) plus extensionless type-322 script '{prepared.AnimationScriptName}'. Developer Tools does not automatically mount arbitrary animation RPacks; deploy the loose SCR and compiled ANM2 objects for editor use.");
        return new CustomModelAnimationLibraryResult(
            outputPath,
            manifestPath,
            Sha256(rpack),
            prepared.AnimationScriptName,
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

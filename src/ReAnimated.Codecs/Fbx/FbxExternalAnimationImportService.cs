using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Domain;

namespace ReAnimated.Codecs.Fbx;

public enum FbxExternalAnimationDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record FbxExternalAnimationDiagnostic(
    string Code,
    FbxExternalAnimationDiagnosticSeverity Severity,
    string Message);

/// <summary>
/// One selectable FBX take. StackObjectId is the stable source identity;
/// display names are never used to merge or deduplicate takes.
/// </summary>
public sealed record FbxExternalAnimationStackDescriptor
{
    public long StackObjectId { get; init; }

    public string Name { get; init; } = string.Empty;

    public ImmutableArray<string> LayerNames { get; init; } = [];

    public long SourceStartTick { get; init; }

    public long SourceStopTick { get; init; }

    public FrameRate FrameRate { get; init; } = new(30, 1);

    public long FrameCount { get; init; }

    public AnimationSourceRoles Roles { get; init; }

    public string? SourceRigId { get; init; }

    public string? SourceRigSignature { get; init; }

    public FbxFacialSourceValueUnit FacialSourceValueUnit { get; init; } =
        FbxFacialSourceValueUnit.Percent;

    public string StackFingerprint { get; init; } = string.Empty;

    public ImmutableArray<FbxExternalAnimationDiagnostic> Diagnostics
    { get; init; } = [];

    public bool RequiresLayerBake => LayerNames.Length != 1;

    public bool CanImport =>
        Roles != AnimationSourceRoles.None &&
        !Diagnostics.Any(static diagnostic =>
            diagnostic.Severity ==
                FbxExternalAnimationDiagnosticSeverity.Error);
}

public sealed record FbxExternalAnimationScanResult(
    ImmutableArray<FbxExternalAnimationStackDescriptor> Stacks);

public sealed record FbxExternalAnimationImportOptions
{
    public FbxCoreAnimationImportOptions Body { get; init; } = new();

    /// <summary>
    /// Explicit DeformPercent interpretation. Percent is deliberately the UI
    /// workflow default; values are never inferred from their numeric range.
    /// </summary>
    public FbxFacialSourceValueUnit FacialSourceValueUnit { get; init; } =
        FbxFacialSourceValueUnit.Percent;

    public IReadOnlyDictionary<string, FbxFacialSourceValueUnit>
        FacialChannelSourceValueUnits
    { get; init; } =
            ImmutableDictionary<string, FbxFacialSourceValueUnit>.Empty;

    public int MaximumFacialChannels { get; init; } = 4_096;

    public int MaximumFacialRawCurveKeys { get; init; } = 1_000_000;

    public int MaximumFacialSampledScalarKeys { get; init; } = 1_000_000;
}

public sealed record FbxExternalAnimationImportResult(
    FbxExternalAnimationStackDescriptor Stack,
    RigDefinition? SourceRig,
    AnimationClip Clip,
    FbxCoreAnimationImportResult? Body,
    FbxFacialAnimationImportResult? Facial,
    FbxFacialSourceValueUnit FacialSourceValueUnit);

/// <summary>
/// Model-independent FBX animation-browser service. It scans every stack,
/// preserves explicit take identities, and imports any checked subset while
/// combining body and DeformPercent curves from the same selected stack.
/// </summary>
public static class FbxExternalAnimationImportService
{
    public static FbxExternalAnimationScanResult Scan(
        FbxBinaryDocument document,
        FbxExternalAnimationImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new FbxExternalAnimationImportOptions();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        FbxSemanticScene scene = FbxSemanticScene.Parse(
            document,
            cancellationToken);
        ImmutableDictionary<long, FbxAnimationStackActivity> activityById =
            scene.AnalyzeAnimationStacks(cancellationToken)
                .ToImmutableDictionary(
                    static activity => activity.Stack.ObjectId);
        HashSet<string> duplicateNames = scene.AnimationStacks
            .GroupBy(static stack => stack.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var rows = ImmutableArray.CreateBuilder<
            FbxExternalAnimationStackDescriptor>(
                scene.AnimationStacks.Length);
        foreach (FbxAnimationStackInfo stack in scene.AnimationStacks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(InspectStack(
                document,
                stack,
                activityById[stack.ObjectId],
                duplicateNames.Contains(stack.Name),
                options,
                cancellationToken));
        }

        return new FbxExternalAnimationScanResult(rows.MoveToImmutable());
    }

    public static async Task<FbxExternalAnimationScanResult> ScanFileAsync(
        string path,
        FbxExternalAnimationImportOptions? options = null,
        FbxReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FbxBinaryDocument document =
            await FbxBinaryReader.ReadFileWithOptionsAsync(
                    path,
                    FbxReadOptions.Animation,
                    limits,
                    cancellationToken)
                .ConfigureAwait(false);
        return Scan(document, options, cancellationToken);
    }

    public static ImmutableArray<FbxExternalAnimationImportResult> ImportSelected(
        FbxBinaryDocument document,
        IEnumerable<long> selectedStackObjectIds,
        FbxExternalAnimationImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selectedStackObjectIds);
        options ??= new FbxExternalAnimationImportOptions();
        ValidateOptions(options);
        long[] selectedIds = selectedStackObjectIds.Distinct().ToArray();
        if (selectedIds.Length == 0)
        {
            return [];
        }

        FbxExternalAnimationScanResult scan = Scan(
            document,
            options,
            cancellationToken);
        Dictionary<long, FbxExternalAnimationStackDescriptor> byId =
            scan.Stacks.ToDictionary(static stack => stack.StackObjectId);
        long[] unknown = selectedIds
            .Where(id => !byId.ContainsKey(id))
            .ToArray();
        if (unknown.Length != 0)
        {
            throw new InvalidDataException(
                "The FBX selection contains unknown animation stack object " +
                "identifiers: " + string.Join(", ", unknown));
        }

        var results = ImmutableArray.CreateBuilder<
            FbxExternalAnimationImportResult>(selectedIds.Length);
        foreach (FbxExternalAnimationStackDescriptor row in scan.Stacks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!selectedIds.Contains(row.StackObjectId))
            {
                continue;
            }

            if (!row.CanImport)
            {
                string details = string.Join(
                    " ",
                    row.Diagnostics
                        .Where(static diagnostic =>
                            diagnostic.Severity ==
                                FbxExternalAnimationDiagnosticSeverity.Error)
                        .Select(static diagnostic => diagnostic.Message));
                throw new InvalidDataException(
                    $"FBX animation stack '{row.Name}' cannot be imported. {details}".Trim());
            }

            results.Add(ImportStack(
                document,
                row,
                options,
                cancellationToken));
        }

        return results.MoveToImmutable();
    }

    public static async Task<ImmutableArray<FbxExternalAnimationImportResult>>
        ImportFileSelectedAsync(
            string path,
            IEnumerable<long> selectedStackObjectIds,
            FbxExternalAnimationImportOptions? options = null,
            FbxReadLimits? limits = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(selectedStackObjectIds);
        FbxBinaryDocument document =
            await FbxBinaryReader.ReadFileWithOptionsAsync(
                    path,
                    FbxReadOptions.Animation,
                    limits,
                    cancellationToken)
                .ConfigureAwait(false);
        return ImportSelected(
            document,
            selectedStackObjectIds,
            options,
            cancellationToken);
    }

    private static FbxExternalAnimationStackDescriptor InspectStack(
        FbxBinaryDocument document,
        FbxAnimationStackInfo stack,
        FbxAnimationStackActivity activity,
        bool duplicateName,
        FbxExternalAnimationImportOptions options,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<
            FbxExternalAnimationDiagnostic>();
        if (duplicateName)
        {
            diagnostics.Add(Error(
                "duplicate_stack_name",
                $"Animation stack name '{stack.Name}' is duplicated; rename the takes so each can be selected deterministically."));
        }

        if (stack.LayerIds.Length != 1)
        {
            diagnostics.Add(Error(
                "layered_stack_requires_bake",
                $"The stack owns {stack.LayerIds.Length} layers; bake or flatten it to one layer before import."));
            return CreateDescriptor(
                stack,
                AnimationSourceRoles.None,
                null,
                null,
                options.FacialSourceValueUnit,
                new FrameRate(30, 1),
                0,
                diagnostics.ToImmutable());
        }

        FbxCoreAnimationImportResult? body = null;
        FbxFacialAnimationImportResult? facial = null;
        AnimationSourceRoles roles = AnimationSourceRoles.None;
        if (!activity.Usable)
        {
            diagnostics.Add(Error(
                "invalid_skeletal_stack",
                activity.UnavailableReason));
        }
        else if (activity.SkeletalBindingCount > 0)
        {
            try
            {
                body = FbxCoreAnimationAdapter.Import(
                    document,
                    BodyOptions(options, stack.Name),
                    cancellationToken);
                roles |= AnimationSourceRoles.Body;
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(Error(
                    "body_import_failed",
                    exception.Message));
            }
        }

        try
        {
            facial = FbxFacialAnimationAdapter.Import(
                document,
                FacialOptions(
                    options,
                    stack.Name,
                    body?.Clip.FrameRate),
                cancellationToken);
            if (facial.Channels.Any(static channel =>
                    channel.Binding is not null))
            {
                roles |= AnimationSourceRoles.Facial;
            }
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error(
                "facial_import_failed",
                exception.Message));
        }

        if (roles == AnimationSourceRoles.None &&
            !diagnostics.Any(static diagnostic =>
                diagnostic.Severity ==
                    FbxExternalAnimationDiagnosticSeverity.Error))
        {
            diagnostics.Add(Error(
                "stack_has_no_animation_channels",
                "The stack has no skeletal or DeformPercent animation channels."));
        }

        FrameRate frameRate = body?.Clip.FrameRate ??
            facial?.Clip.FrameRate ??
            new FrameRate(30, 1);
        long frameCount = Math.Max(
            body?.Clip.FrameCount ?? 0,
            facial?.Clip.FrameCount ?? 0);
        if ((roles & (AnimationSourceRoles.Body |
                      AnimationSourceRoles.Facial)) ==
            (AnimationSourceRoles.Body | AnimationSourceRoles.Facial))
        {
            diagnostics.Add(new FbxExternalAnimationDiagnostic(
                "combined_body_facial_stack",
                FbxExternalAnimationDiagnosticSeverity.Information,
                "Skeletal and DeformPercent curves will be merged into one immutable source clip."));
        }

        string? rigSignature = body is null
            ? null
            : RigSignature.Compute(body.Rig);
        return CreateDescriptor(
            stack,
            roles,
            body?.Rig.Id,
            rigSignature,
            options.FacialSourceValueUnit,
            frameRate,
            frameCount,
            diagnostics.ToImmutable());
    }

    private static FbxExternalAnimationImportResult ImportStack(
        FbxBinaryDocument document,
        FbxExternalAnimationStackDescriptor row,
        FbxExternalAnimationImportOptions options,
        CancellationToken cancellationToken)
    {
        FbxCoreAnimationImportResult? body = null;
        FbxFacialAnimationImportResult? facial = null;
        if ((row.Roles & AnimationSourceRoles.Body) != 0)
        {
            body = FbxCoreAnimationAdapter.Import(
                document,
                BodyOptions(options, row.Name),
                cancellationToken);
        }

        if ((row.Roles & AnimationSourceRoles.Facial) != 0)
        {
            facial = FbxFacialAnimationAdapter.Import(
                document,
                FacialOptions(
                    options,
                    row.Name,
                    body?.Clip.FrameRate),
                cancellationToken);
        }

        AnimationClip clip = MergeClips(row.Name, body, facial);
        return new FbxExternalAnimationImportResult(
            row,
            body?.Rig,
            clip,
            body,
            facial,
            options.FacialSourceValueUnit);
    }

    private static AnimationClip MergeClips(
        string name,
        FbxCoreAnimationImportResult? body,
        FbxFacialAnimationImportResult? facial)
    {
        if (body is null)
        {
            return facial!.Clip;
        }

        if (facial is null)
        {
            return body.Clip;
        }

        if (body.Clip.FrameRate != facial.Clip.FrameRate)
        {
            throw new InvalidDataException(
                $"FBX animation stack '{name}' body and facial samplers produced different frame rates.");
        }

        long frameCount = Math.Max(
            body.Clip.FrameCount,
            facial.Clip.FrameCount);
        return new AnimationClip(
            name,
            body.Clip.FrameRate,
            frameCount,
            body.Clip.TransformTracks,
            facial.Clip.ScalarTracks,
            body.Clip.AuxiliaryTransformTracks);
    }

    private static FbxCoreAnimationImportOptions BodyOptions(
        FbxExternalAnimationImportOptions options,
        string stackName) =>
        options.Body with { AnimationStackName = stackName };

    private static FbxFacialAnimationImportOptions FacialOptions(
        FbxExternalAnimationImportOptions options,
        string stackName,
        FrameRate? frameRate) =>
        new()
        {
            AnimationStackName = stackName,
            SamplingFrameRate = frameRate ?? options.Body.SamplingFrameRate,
            DefaultSourceValueUnit = options.FacialSourceValueUnit,
            ChannelSourceValueUnits =
                options.FacialChannelSourceValueUnits,
            MaximumChannels = options.MaximumFacialChannels,
            MaximumRawCurveKeys = options.MaximumFacialRawCurveKeys,
            MaximumSampleFrames = options.Body.MaximumSampleFrames,
            MaximumSampledScalarKeys =
                options.MaximumFacialSampledScalarKeys,
        };

    private static FbxExternalAnimationStackDescriptor CreateDescriptor(
        FbxAnimationStackInfo stack,
        AnimationSourceRoles roles,
        string? rigId,
        string? rigSignature,
        FbxFacialSourceValueUnit facialSourceValueUnit,
        FrameRate frameRate,
        long frameCount,
        ImmutableArray<FbxExternalAnimationDiagnostic> diagnostics)
    {
        string fingerprintMaterial = string.Join(
            "|",
            "dlra-external-fbx-stack-v1",
            stack.ObjectId,
            stack.Name,
            string.Join(",", stack.LayerIds),
            stack.StartTick,
            stack.StopTick,
            (int)roles,
            rigSignature ?? "no-rig");
        return new FbxExternalAnimationStackDescriptor
        {
            StackObjectId = stack.ObjectId,
            Name = stack.Name,
            LayerNames = stack.LayerNames,
            SourceStartTick = stack.StartTick,
            SourceStopTick = stack.StopTick,
            FrameRate = frameRate,
            FrameCount = frameCount,
            Roles = roles,
            SourceRigId = rigId,
            SourceRigSignature = rigSignature,
            FacialSourceValueUnit = facialSourceValueUnit,
            StackFingerprint = Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(fingerprintMaterial)))
                .ToLowerInvariant(),
            Diagnostics = diagnostics,
        };
    }

    private static FbxExternalAnimationDiagnostic Error(
        string code,
        string message) =>
        new(
            code,
            FbxExternalAnimationDiagnosticSeverity.Error,
            message);

    private static void ValidateOptions(
        FbxExternalAnimationImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Body);
        if (options.FacialSourceValueUnit is not (
                FbxFacialSourceValueUnit.Normalized or
                FbxFacialSourceValueUnit.Percent))
        {
            throw new ArgumentException(
                "External FBX facial import requires an explicit normalized or percent DeformPercent unit.",
                nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MaximumFacialChannels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MaximumFacialRawCurveKeys);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MaximumFacialSampledScalarKeys);
    }
}

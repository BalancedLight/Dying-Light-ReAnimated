using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReAnimated.Core.Domain;

namespace ReAnimated.Core.Project;

public class ProjectFormatException : FormatException
{
    public ProjectFormatException()
    {
    }

    public ProjectFormatException(string? message)
        : base(message)
    {
    }

    public ProjectFormatException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

public sealed class LegacyProjectFormatException : ProjectFormatException
{
    public LegacyProjectFormatException()
    {
    }

    public LegacyProjectFormatException(string? message)
        : base(message)
    {
    }

    public LegacyProjectFormatException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public LegacyProjectFormatException(int detectedSchemaVersion)
        : base(
            $"Project schema {detectedSchemaVersion} belongs to the legacy Python application. " +
            "This C# first pass neither imports, modifies, nor overwrites legacy projects; " +
            "create a fresh C# schema-3 project instead.")
    {
        DetectedSchemaVersion = detectedSchemaVersion;
    }

    public int? DetectedSchemaVersion { get; }
}

/// <summary>
/// Reads schema-1/2 C# projects, migrates them in memory, and atomically writes
/// the DL1-only schema-3 project format.
/// </summary>
public static class ProjectSerializer
{
    public const long MaximumProjectBytes = 64L * 1024L * 1024L;

    private const string LegacyReconstructionScorer =
        "legacy-schema1-reconstruction-v1";

    private const string LegacyExplicitReviewScorer =
        "legacy-schema1-explicit-review-v1";

    private static readonly string[] RequiredSchema1RootProperties =
    [
        "schemaVersion",
        "format",
        "projectId",
        "name",
        "game",
        "assets",
        "animations",
        "dl1Settings",
        "previewMode",
        "previewProfile",
    ];

    private static readonly string[] RequiredSchema2RootProperties =
    [
        .. RequiredSchema1RootProperties,
        "models",
        "animationSources",
        "animationVariants",
        "workflow",
    ];

    private static readonly string[] RequiredSchema3RootProperties =
    [
        .. RequiredSchema2RootProperties,
        "animationLibraries",
        "exportSelection",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static DlraProject Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumProjectBytes)
            {
                throw new ProjectFormatException(
                    $"Project files cannot exceed {MaximumProjectBytes} bytes.");
            }

            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ProjectFormatException("A project document must contain a JSON object.");
            }

            if (root.TryGetProperty("schema_version", out JsonElement legacySchemaElement))
            {
                int? legacyVersion = legacySchemaElement.TryGetInt32(out int detectedVersion)
                    ? detectedVersion
                    : null;
                throw legacyVersion.HasValue
                    ? new LegacyProjectFormatException(legacyVersion.Value)
                    : new LegacyProjectFormatException(
                        "A legacy snake_case schema marker is not accepted by the C# application.");
            }

            if (!root.TryGetProperty("schemaVersion", out JsonElement schemaElement) ||
                !schemaElement.TryGetInt32(out int schemaVersion))
            {
                throw new ProjectFormatException("The project does not declare an integer schemaVersion.");
            }

            if (schemaVersion is not (1 or 2 or DlraProject.CurrentSchemaVersion))
            {
                throw new ProjectFormatException(
                    $"Project schema {schemaVersion} is not supported by this application.");
            }

            ValidateRequiredRootProperties(root, schemaVersion);
            stream.Position = 0;
            DlraProject project = JsonSerializer.Deserialize<DlraProject>(
                stream,
                SerializerOptions) ??
                throw new ProjectFormatException("The project document was empty.");
            project = schemaVersion switch
            {
                1 => MigrateSchema1(project),
                2 => MigrateSchema2(project),
                DlraProject.CurrentSchemaVersion => NormalizeSchema3(project),
                _ => throw new UnreachableException(),
            };
            project.Validate();
            return project;
        }
        catch (ProjectFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProjectFormatException("The project contains invalid JSON.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException("The project contains invalid values.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ProjectFormatException("The project contains invalid domain state.", exception);
        }
    }

    public static string SaveAtomic(DlraProject project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        DlraProject normalized = project.SchemaVersion switch
        {
            1 => MigrateSchema1(project),
            2 => MigrateSchema2(project),
            DlraProject.CurrentSchemaVersion => NormalizeSchema3(project),
            _ => throw new ProjectFormatException(
                $"Project schema {project.SchemaVersion} is not supported by this application."),
        };
        normalized.Validate();

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The project path must have a parent directory.", nameof(path));
        }

        EnsureExistingTargetCanBeReplaced(fullPath);
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, normalized, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
            return fullPath;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void EnsureExistingTargetCanBeReplaced(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return;
        }

        try
        {
            // A save may replace only a project already owned by this fresh
            // C# schema. In particular, a Save As choice must never turn the
            // file-dialog overwrite prompt into migration or destruction of
            // a legacy Python schema-1..10 project.
            _ = Load(fullPath);
        }
        catch (LegacyProjectFormatException)
        {
            throw;
        }
        catch (ProjectFormatException exception)
        {
            throw new ProjectFormatException(
                "Refusing to overwrite an existing .dlraproj that is not a valid " +
                "DL ReAnimated C# schema-1, schema-2, or schema-3 project.",
                exception);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }

    internal static DlraProject MigrateSchema1(
        DlraProject project,
        bool preserveSourceOnlyCompatibilityRows = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        Dictionary<Guid, ProjectAssetReference> assets = project.Assets
            .ToDictionary(static asset => asset.Id);
        ValidateSchema1AssetReferences(project, assets);
        project.ModelsWorkspace?.Validate(
            assets.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Kind));
        ImmutableArray<ProjectAnimation> animations = project.Animations
            .Select(animation => NormalizeLegacyAnimationProvenance(
                NormalizeAnimation(animation, assets)))
            .ToImmutableArray();

        ImmutableArray<ProjectModelEntry> models = InferModels(
            project,
            animations,
            assets);
        Dictionary<Guid, ProjectModelEntry> modelByAsset = models
            .ToDictionary(static model => model.AssetId);
        List<ProjectAnimationSource> sources = [];
        List<ProjectAnimationVariant> variants = [];
        var sourceIdByLegacyAnimationId = new Dictionary<Guid, Guid>();
        foreach (IGrouping<Guid, ProjectAnimation> group in animations
                     .GroupBy(static animation =>
                         animation.VariantGroupId ?? animation.Id))
        {
            List<IGrouping<string, ProjectAnimation>> identities = group
                .GroupBy(CreateLegacySourceIdentityKey)
                .OrderBy(static identity => identity.Key, StringComparer.Ordinal)
                .ToList();
            bool conflicting = identities.Count > 1;
            foreach (IGrouping<string, ProjectAnimation> identity in identities)
            {
                ProjectAnimation representative = identity
                    .OrderBy(static animation => animation.Id)
                    .First();
                Guid sourceId = conflicting
                    ? CreateDeterministicGuid(
                        "dlra-schema2-conflicting-source-v1",
                        group.Key.ToString("N"),
                        identity.Key)
                    : group.Key;
                ProjectAnimationSource source = ToSchema2Source(
                    representative,
                    sourceId,
                    group.Key,
                    conflicting);
                sources.Add(source);
                foreach (ProjectAnimation animation in identity)
                {
                    sourceIdByLegacyAnimationId[animation.Id] = sourceId;
                    if (animation.TargetAssetId is null)
                    {
                        // Schema 1 allowed a source to exist before a target
                        // was chosen. Schema 2 represents that state with the
                        // immutable source alone; it must not manufacture an
                        // unbound variant.
                        continue;
                    }

                    variants.Add(ToSchema2Variant(
                        animation,
                        sourceId,
                        modelByAsset));
                }
            }
        }

        Guid? activeAnimationId = project.ActiveAnimationId is { } legacyId &&
                                  variants.Any(variant => variant.Id == legacyId)
            ? legacyId
            : variants.FirstOrDefault()?.Id;
        ProjectAnimationVariant? selectedVariant = activeAnimationId is { } id
            ? variants.FirstOrDefault(variant => variant.Id == id)
            : null;
        Guid? selectedSourceId = selectedVariant?.SourceId;
        if (selectedSourceId is null &&
            project.ActiveAnimationId is { } activeLegacyId)
        {
            sourceIdByLegacyAnimationId.TryGetValue(
                activeLegacyId,
                out Guid activeSourceId);
            selectedSourceId = activeSourceId == Guid.Empty
                ? null
                : activeSourceId;
        }

        selectedSourceId ??= sources.FirstOrDefault()?.Id;

        ProjectWorkflowTab activeTab =
            project.ModelsWorkspace is not null
                ? ProjectWorkflowTab.Models
                : activeAnimationId is not null
                    ? ProjectWorkflowTab.Playback
                    : sources.Count > 0
                        ? ProjectWorkflowTab.Animations
                        : ProjectWorkflowTab.Models;
        Guid? selectedModelId = project.ModelsWorkspace is { } workspace &&
                                modelByAsset.TryGetValue(
                                    workspace.PackageAssetId,
                                    out ProjectModelEntry? workspaceModel)
            ? workspaceModel.Id
            : selectedVariant?.TargetModelId;

        DlraProject schema2 = project with
        {
            SchemaVersion = 2,
            Models = models,
            AnimationSources = sources.ToImmutableArray(),
            AnimationVariants = variants.ToImmutableArray(),
            AnimationLibraries = [],
            ExportSelection = new ProjectExportSelection(),
            Animations = animations
                .Where(animation =>
                    preserveSourceOnlyCompatibilityRows &&
                    animation.TargetAssetId is null ||
                    variants.Any(variant => variant.Id == animation.Id))
                .ToImmutableArray(),
            ActiveAnimationId = activeAnimationId,
            Workflow = new ProjectWorkflowState
            {
                ActiveTab = activeTab,
                SelectedModelId = selectedModelId,
                SelectedAnimationSourceId = selectedSourceId,
                SelectedAnimationVariantId = selectedVariant?.Id,
            },
        };

        return NormalizeSchema3Core(
            schema2,
            seedLegacyExportSelection: true,
            allowLegacyProjectionFallback: false);
    }

    internal static DlraProject MigrateSchema2(DlraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        // These references did not exist in schema 2. A hand-edited or
        // down-versioned document can still carry serializer-readable schema
        // 3 values whose referenced arrays are absent; never let those stale
        // IDs suppress deterministic schema-3 library allocation.
        DlraProject schema2 = project with
        {
            Models = project.Models
                .Select(static model => model with
                {
                    RootAnimationLibraryId = null,
                })
                .ToImmutableArray(),
            AnimationVariants = project.AnimationVariants
                .Select(static variant => variant with
                {
                    OwningAnimationLibraryId = null,
                    OutputAnm2Name = null,
                })
                .ToImmutableArray(),
            AnimationLibraries = [],
            ExportSelection = new ProjectExportSelection(),
        };
        return NormalizeSchema3Core(
            schema2,
            seedLegacyExportSelection: true,
            allowLegacyProjectionFallback: true);
    }

    internal static DlraProject NormalizeSchema3(DlraProject project) =>
        NormalizeSchema3Core(
            project,
            seedLegacyExportSelection: false,
            allowLegacyProjectionFallback: true);

    private static DlraProject NormalizeSchema3Core(
        DlraProject project,
        bool seedLegacyExportSelection,
        bool allowLegacyProjectionFallback)
    {
        ArgumentNullException.ThrowIfNull(project);
        Dictionary<Guid, ProjectAssetReference> assets = project.Assets
            .ToDictionary(static asset => asset.Id);
        Dictionary<Guid, ProjectModelEntry> models = project.Models
            .ToDictionary(static model => model.Id);
        ImmutableArray<ProjectAnimationSource> sources =
            project.AnimationSources
                .Select(source => NormalizeSourcePresentation(
                    source.SourceAnimationSkeletonSignature is null &&
                    source.EmbeddedCustomModelStack is
                    {
                        SourceAnimationSkeletonSignature: { } signature,
                    }
                        ? source with
                        {
                            SourceAnimationSkeletonSignature = signature,
                        }
                        : source,
                    assets,
                    models))
                .ToImmutableArray();
        Dictionary<Guid, ProjectAnimationSource> sourceById = sources
            .ToDictionary(static source => source.Id);
        ImmutableArray<ProjectAnimationVariant> variants =
            project.AnimationVariants
                .Select(variant => NormalizeVariantBinding(
                    NormalizeVariantProvenance(variant),
                    sourceById.GetValueOrDefault(variant.SourceId)))
                .ToImmutableArray();

        // App integration remains source compatible: projects constructed by
        // the schema-1 WPF surface are promoted before validation/save.
        if (allowLegacyProjectionFallback &&
            sources.IsEmpty &&
            variants.IsEmpty &&
            (!project.Animations.IsEmpty ||
             project.ModelsWorkspace is not null))
        {
            return MigrateSchema1(
                project with { SchemaVersion = 1 },
                preserveSourceOnlyCompatibilityRows: true);
        }

        project = ProjectAnimationOutputNormalizer.Normalize(project with
        {
            AnimationSources = sources,
            AnimationVariants = variants,
        });
        variants = project.AnimationVariants;
        ValidateSchema2References(project, sources, variants);

        ProjectExportSelection exportSelection =
            seedLegacyExportSelection
                ? CreateLegacyExportSelection(variants)
                : project.ExportSelection;
        return project with
        {
            SchemaVersion = DlraProject.CurrentSchemaVersion,
            AnimationSources = sources,
            AnimationVariants = variants,
            Animations = SynchronizeCompatibilityAnimations(
                project,
                sources,
                variants),
            ExportSelection = exportSelection,
            // A model-first import creates owning direct variants immediately,
            // but it must not silently turn one of them into the active
            // animation target.  Only an explicit activation (or an existing
            // persisted workflow selection) restores an active variant.
            ActiveAnimationId = project.ActiveAnimationId ??
                project.Workflow.SelectedAnimationVariantId,
        };
    }

    private static ProjectAnimationSource NormalizeSourcePresentation(
        ProjectAnimationSource source,
        Dictionary<Guid, ProjectAssetReference> assets,
        Dictionary<Guid, ProjectModelEntry> models)
    {
        if (source.Presentation is not null)
        {
            return source;
        }

        assets.TryGetValue(
            source.SourceAssetId,
            out ProjectAssetReference? sourceAsset);
        ProjectModelEntry? owningModel = source.EmbeddedCustomModelStack is null
            ? null
            : models.Values.FirstOrDefault(model =>
                model.AssetId == source.SourceAssetId);
        ProjectAssetReference? boundModelAsset =
            source.SourceBinding?.RetailSourceModelAssetId is { } modelAssetId
                ? assets.GetValueOrDefault(modelAssetId)
                : null;

        ProjectAnimationSourceOriginKind originKind;
        string originName;
        Guid? owningModelId = null;
        if (owningModel is not null)
        {
            originKind = ProjectAnimationSourceOriginKind.OwningCustomModel;
            originName = owningModel.Name;
            owningModelId = owningModel.Id;
        }
        else if (boundModelAsset is not null ||
                 source.SourceBinding?.Kind == AnimationSourceKind.RetailAnm2)
        {
            originKind = boundModelAsset?.Kind ==
                    ProjectAssetKind.CustomModelSource
                ? ProjectAnimationSourceOriginKind.BoundProjectModel
                : ProjectAnimationSourceOriginKind.BoundRetailModel;
            originName = boundModelAsset?.RetailIdentity?.ResourceName ??
                (boundModelAsset is null
                    ? null
                    : Path.GetFileNameWithoutExtension(
                        boundModelAsset.RelativePath)) ??
                sourceAsset?.RetailIdentity?.ResourceName ??
                source.Name;
        }
        else if (source.SourceBinding?.Kind == AnimationSourceKind.LocalFbx ||
                 source.EmbeddedCustomModelStack is not null)
        {
            originKind = ProjectAnimationSourceOriginKind.ImportedFbxRig;
            originName = sourceAsset is null
                ? source.Name
                : Path.GetFileNameWithoutExtension(sourceAsset.RelativePath);
        }
        else
        {
            originKind = ProjectAnimationSourceOriginKind.UnresolvedLegacy;
            originName = source.Name;
        }

        if (string.IsNullOrWhiteSpace(originName))
        {
            originName = source.Name;
        }

        return source with
        {
            Presentation = new ProjectAnimationSourcePresentation
            {
                OriginKind = originKind,
                OriginName = originName,
                OwningModelId = owningModelId,
                ProjectAssetId = source.SourceAssetId,
                SourceRigIdentity = string.IsNullOrWhiteSpace(
                    source.SourceRigSignature)
                    ? null
                    : source.SourceRigSignature,
            },
        };
    }

    private static ProjectExportSelection CreateLegacyExportSelection(
        ImmutableArray<ProjectAnimationVariant> variants)
    {
        ProjectAnimationVariant[] included = variants
            .Where(static variant => variant.IncludeInPackage)
            .OrderBy(static variant => variant.Id)
            .ToArray();
        return new ProjectExportSelection
        {
            ModelIds = included
                .Select(static variant => variant.TargetModelId)
                .Distinct()
                .Order()
                .ToImmutableArray(),
            AnimationVariantIds = included
                .Select(static variant => variant.Id)
                .ToImmutableArray(),
        };
    }

    private static ImmutableArray<ProjectAnimation> SynchronizeCompatibilityAnimations(
        DlraProject project,
        ImmutableArray<ProjectAnimationSource> sources,
        ImmutableArray<ProjectAnimationVariant> variants)
    {
        Dictionary<Guid, ProjectAnimationSource> sourceById =
            sources.ToDictionary(static source => source.Id);
        Dictionary<Guid, ProjectModelEntry> modelById = project.Models
            .ToDictionary(static model => model.Id);
        Dictionary<Guid, ProjectAnimation> persistedCompatibility =
            project.Animations.ToDictionary(static animation => animation.Id);
        var rows = ImmutableArray.CreateBuilder<ProjectAnimation>();
        foreach (ProjectAnimationVariant variant in variants)
        {
            if (!sourceById.TryGetValue(
                    variant.SourceId,
                    out ProjectAnimationSource? source))
            {
                throw new ProjectFormatException(
                    $"Animation variant '{variant.Name}' refers to unknown source '{variant.SourceId}'.");
            }

            if (!modelById.TryGetValue(
                    variant.TargetModelId,
                    out ProjectModelEntry? targetModel))
            {
                throw new ProjectFormatException(
                    $"Animation variant '{variant.Name}' refers to unknown target model '{variant.TargetModelId}'.");
            }

            if (source.SourceBinding is null)
            {
                if (source.RequiresSourceRebind &&
                    persistedCompatibility.TryGetValue(
                        variant.Id,
                        out ProjectAnimation? unresolved))
                {
                    rows.Add(NormalizeLegacyAnimationProvenance(unresolved));
                }

                // Embedded custom-model stacks and unresolved schema-1 ANM2
                // sources have no newly manufactured legacy representation.
                continue;
            }

            Guid targetAssetId = targetModel.AssetId;
            rows.Add(new ProjectAnimation
            {
                Id = variant.Id,
                VariantGroupId = source.Id,
                Name = variant.Name,
                SourceAssetId = source.SourceAssetId,
                SourceBinding = source.SourceBinding,
                MimicAssetId = source.MimicAssetId,
                FacialAnimationSourceBinding =
                    source.FacialAnimationSourceBinding,
                FacialSourceAssetId = source.FacialSourceAssetId,
                FacialSourceValueUnit = source.FacialSourceValueUnit,
                FacialTiming = source.FacialTiming,
                TargetAssetId = targetAssetId,
                TargetRigId = variant.TargetRigId,
                SourceRigSignature = source.SourceRigSignature,
                TargetRigSignature = variant.TargetRigSignature,
                SourceAnimationSkeletonSignature =
                    source.SourceAnimationSkeletonSignature,
                TargetAnimationSkeletonSignature =
                    variant.TargetAnimationSkeletonSignature,
                BindingMode = variant.BindingMode,
                DirectBinding = variant.DirectBinding,
                BindingEvidenceFingerprint =
                    variant.BindingEvidenceFingerprint,
                BindingPolicyVersion = variant.BindingPolicyVersion,
                MappingFingerprint = variant.MappingFingerprint,
                MimicProfileId = variant.MimicProfileId,
                MimicMappingFingerprint = variant.MimicMappingFingerprint,
                FrameRate = source.FrameRate,
                FrameCount = source.FrameCount,
                RootMotionMode = variant.RootMotionMode,
                RootBoneName = variant.RootBoneName,
                PreviewMotionAccumulationEnabled =
                    variant.PreviewMotionAccumulationEnabled,
                BoneMappings = variant.BoneMappings,
                TargetBindReviews = variant.TargetBindReviews,
                EditLayers = variant.EditLayers,
                MorphBindings = variant.MorphBindings,
                MorphEditLayers = variant.MorphEditLayers,
                IkLayers = variant.IkLayers,
                Attachments = variant.Attachments,
            });
        }

        // The compatibility surface can still contain a source-only draft
        // while the user is browsing models or before an explicit target has
        // been assigned. Schema 2 deliberately does not manufacture an
        // unbound variant for that state, but dropping this row would erase
        // legacy facial attachments and authored edit layers that the WPF
        // surface must retain until a target variant is created.
        HashSet<Guid> variantIds = variants
            .Select(static variant => variant.Id)
            .ToHashSet();
        foreach (ProjectAnimation draft in project.Animations
                     .Where(animation =>
                         animation.TargetAssetId is null &&
                         !variantIds.Contains(animation.Id)))
        {
            Guid sourceId = draft.VariantGroupId ?? draft.Id;
            if (!sourceById.TryGetValue(
                    sourceId,
                    out ProjectAnimationSource? source) &&
                !sources.Any(candidate =>
                    candidate.LegacyVariantGroupId == sourceId &&
                    candidate.SourceAssetId == draft.SourceAssetId))
            {
                continue;
            }

            rows.Add(NormalizeLegacyAnimationProvenance(draft));
        }

        return rows.ToImmutable();
    }

    private static ImmutableArray<ProjectModelEntry> InferModels(
        DlraProject project,
        ImmutableArray<ProjectAnimation> animations,
        Dictionary<Guid, ProjectAssetReference> assets)
    {
        var modelAssetIds = new HashSet<Guid>(
            animations
                .Where(static animation => animation.TargetAssetId.HasValue)
                .Select(static animation => animation.TargetAssetId!.Value));
        if (project.ModelsWorkspace is { } workspace)
        {
            modelAssetIds.Add(workspace.PackageAssetId);
        }

        var models = ImmutableArray.CreateBuilder<ProjectModelEntry>(
            modelAssetIds.Count);
        foreach (Guid assetId in modelAssetIds.Order())
        {
            if (!assets.TryGetValue(
                    assetId,
                    out ProjectAssetReference? asset))
            {
                throw new ProjectFormatException(
                    $"Schema-1 model reference '{assetId}' does not exist in the project asset library.");
            }

            string name = asset.RetailIdentity?.ResourceName ??
                Path.GetFileNameWithoutExtension(asset.RelativePath);
            ProjectAnimation[] targets = animations
                .Where(animation => animation.TargetAssetId == assetId)
                .ToArray();
            string[] targetRigSignatures = targets
                .Select(static animation => animation.TargetRigSignature)
                .Where(IsSha256)
                .Select(static signature => signature!.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (targets.Length > 0 &&
                (targetRigSignatures.Length != 1 ||
                 targets.Any(animation =>
                     !IsSha256(animation.TargetRigSignature))))
            {
                throw new ProjectFormatException(
                    $"Schema-1 target model '{name}' does not have one consistent SHA-256 rig signature and cannot be migrated safely.");
            }

            models.Add(new ProjectModelEntry
            {
                Id = CreateDeterministicGuid(
                    "dlra-schema2-model-v1",
                    assetId.ToString("N")),
                AssetId = assetId,
                Name = string.IsNullOrWhiteSpace(name)
                    ? "Project model"
                    : name,
                RigSignature = targetRigSignatures.FirstOrDefault(),
                // A schema-1 Models-workspace-only package did not persist a
                // rig contract in the project file. Keep it browsable but
                // fail closed as a static target until the package is decoded
                // and the model entry is refreshed by the editor.
                IsStatic = targets.Length == 0,
            });
        }

        return models.MoveToImmutable();
    }

    private static ProjectAnimationSource ToSchema2Source(
        ProjectAnimation animation,
        Guid sourceId,
        Guid legacyGroupId,
        bool conflicting) =>
        new()
        {
            Id = sourceId,
            Name = animation.Name,
            SourceAssetId = animation.SourceAssetId,
            SourceBinding = animation.SourceBinding,
            RequiresSourceRebind = animation.SourceBinding is null,
            LegacySourceRigSignature = animation.SourceBinding is null
                ? animation.SourceRigSignature
                : null,
            MimicAssetId = animation.MimicAssetId,
            FacialAnimationSourceBinding =
                animation.FacialAnimationSourceBinding,
            FacialSourceAssetId = animation.FacialSourceAssetId,
            FacialSourceValueUnit = animation.FacialSourceValueUnit,
            FacialTiming = animation.FacialTiming,
            SourceAnimationSkeletonSignature =
                animation.SourceAnimationSkeletonSignature,
            FrameRate = animation.FrameRate,
            FrameCount = animation.FrameCount,
            LegacyVariantGroupId = legacyGroupId,
            MigrationNote = conflicting
                ? "The legacy variant group contained conflicting immutable source identities and was split without guessing."
                : null,
        };

    private static ProjectAnimationVariant ToSchema2Variant(
        ProjectAnimation animation,
        Guid sourceId,
        Dictionary<Guid, ProjectModelEntry> modelByAsset)
    {
        if (animation.TargetAssetId is not { } targetAssetId ||
            !modelByAsset.TryGetValue(
                targetAssetId,
                out ProjectModelEntry? targetModel))
        {
            throw new ProjectFormatException(
                $"Schema-1 animation '{animation.Name}' refers to an unknown target-model asset.");
        }

        return NormalizeVariantProvenance(new ProjectAnimationVariant
        {
            Id = animation.Id,
            SourceId = sourceId,
            Name = animation.Name,
            TargetModelId = targetModel.Id,
            TargetRigId = animation.TargetRigId,
            TargetRigSignature = animation.TargetRigSignature,
            TargetAnimationSkeletonSignature =
                animation.TargetAnimationSkeletonSignature,
            BindingMode = animation.DirectBinding is not null
                ? ProjectAnimationBindingMode.CompatibleDirect
                : !string.Equals(
                    animation.SourceRigSignature,
                    animation.TargetRigSignature,
                    StringComparison.OrdinalIgnoreCase)
                    ? ProjectAnimationBindingMode.Retarget
                    : ProjectAnimationBindingMode.ExactDirect,
            DirectBinding = animation.DirectBinding,
            BindingEvidenceFingerprint =
                animation.BindingEvidenceFingerprint,
            BindingPolicyVersion = animation.BindingPolicyVersion,
            MappingFingerprint = animation.MappingFingerprint,
            MimicProfileId = animation.MimicProfileId,
            MimicMappingFingerprint = animation.MimicMappingFingerprint,
            RootMotionMode = animation.RootMotionMode,
            RootBoneName = animation.RootBoneName,
            PreviewMotionAccumulationEnabled =
                animation.PreviewMotionAccumulationEnabled,
            BoneMappings = animation.BoneMappings,
            TargetBindReviews = animation.TargetBindReviews,
            EditLayers = animation.EditLayers,
            MorphBindings = animation.MorphBindings,
            MorphEditLayers = animation.MorphEditLayers,
            IkLayers = animation.IkLayers,
            Attachments = animation.Attachments,
        });
    }

    private static ProjectAnimationVariant NormalizeVariantProvenance(
        ProjectAnimationVariant variant) =>
        variant with
        {
            BoneMappings = variant.BoneMappings
                .Select(NormalizeBoneMappingProvenance)
                .ToImmutableArray(),
            MorphBindings = variant.MorphBindings
                .Select(NormalizeMorphBindingProvenance)
                .ToImmutableArray(),
        };

    private static ProjectAnimationVariant NormalizeVariantBinding(
        ProjectAnimationVariant variant,
        ProjectAnimationSource? source)
    {
        if (variant.DirectBinding is { } direct)
        {
            return variant with
            {
                BindingMode = ProjectAnimationBindingMode.CompatibleDirect,
                TargetAnimationSkeletonSignature =
                    variant.TargetAnimationSkeletonSignature ??
                    direct.TargetSkeletonSignature,
                BindingEvidenceFingerprint = direct.EvidenceFingerprint,
                BindingPolicyVersion = direct.Policy,
            };
        }

        bool runtimeRigDiffers = source is not null &&
            !string.IsNullOrWhiteSpace(source.SourceRigSignature) &&
            !string.IsNullOrWhiteSpace(variant.TargetRigSignature) &&
            !string.Equals(
                source.SourceRigSignature,
                variant.TargetRigSignature,
                StringComparison.OrdinalIgnoreCase);
        bool retargetEvidence =
            // A different runtime rig is direct only when a persisted,
            // validated DirectRigBinding proves the compatible correspondence.
            // Matching skeleton fingerprints alone cannot provide the row-level
            // identity and topology evidence required by the direct evaluator.
            runtimeRigDiffers ||
            !variant.BoneMappings.IsEmpty ||
            !variant.TargetBindReviews.IsEmpty ||
            variant.MappingFingerprint is not null;
        return variant with
        {
            BindingMode = retargetEvidence
                ? ProjectAnimationBindingMode.Retarget
                : ProjectAnimationBindingMode.ExactDirect,
            DirectBinding = null,
            BindingEvidenceFingerprint = null,
            BindingPolicyVersion = null,
        };
    }

    private static ProjectAnimation NormalizeLegacyAnimationProvenance(
        ProjectAnimation animation) =>
        animation with
        {
            BoneMappings = animation.BoneMappings
                .Select(NormalizeBoneMappingProvenance)
                .ToImmutableArray(),
            MorphBindings = animation.MorphBindings
                .Select(NormalizeMorphBindingProvenance)
                .ToImmutableArray(),
        };

    private static ProjectBoneMapping NormalizeBoneMappingProvenance(
        ProjectBoneMapping mapping)
    {
        mapping = mapping.TransformComponents is null
            ? mapping with
            {
                TransformComponents =
                    RetargetTransformComponentsCompatibility.FromLegacy(
                        mapping.ComponentPolicy),
            }
            : mapping;
        if (mapping.Evidence is null ||
            mapping.ScorerVersion is null ||
            mapping.EvidenceFingerprint is null)
        {
            return mapping;
        }

        string storedEvidence = mapping.Evidence;
        string scorerVersion = mapping.ScorerVersion;
        string evidenceFingerprint = mapping.EvidenceFingerprint;
        if (mapping.ReviewOrigin != ProjectMappingReviewOrigin.None ||
            !string.Equals(
                scorerVersion,
                "unscored-v1",
                StringComparison.Ordinal) ||
            !string.Equals(
                storedEvidence,
                "No mapping evidence recorded.",
                StringComparison.Ordinal) ||
            (evidenceFingerprint.Length > 0 &&
             evidenceFingerprint.Any(static value => value != '0')))
        {
            return mapping;
        }

        (double confidence, string evidence) =
            ReconstructLegacyMappingEvidence(mapping.Method);
        ProjectMappingReviewOrigin origin = mapping.IsReviewed
            ? ProjectMappingReviewOrigin.Explicit
            : ProjectMappingReviewOrigin.None;
        string scorer = mapping.IsReviewed
            ? LegacyExplicitReviewScorer
            : LegacyReconstructionScorer;
        if (mapping.IsReviewed)
        {
            evidence =
                "Legacy schema-1 row preserved as an explicit author review. " +
                evidence;
        }

        return mapping with
        {
            Confidence = confidence,
            Evidence = evidence,
            ReviewOrigin = origin,
            ScorerVersion = scorer,
            EvidenceFingerprint = CreateEvidenceFingerprint(
                "bone",
                mapping.SourceBoneName,
                mapping.TargetBoneName,
                mapping.Method,
                confidence,
                evidence,
                scorer),
        };
    }

    private static ProjectMorphBinding NormalizeMorphBindingProvenance(
        ProjectMorphBinding binding)
    {
        if (binding.Evidence is null ||
            binding.ScorerVersion is null ||
            binding.EvidenceFingerprint is null)
        {
            return binding;
        }

        string storedEvidence = binding.Evidence;
        string scorerVersion = binding.ScorerVersion;
        string evidenceFingerprint = binding.EvidenceFingerprint;
        if (binding.ReviewOrigin != ProjectMappingReviewOrigin.None ||
            !string.Equals(
                scorerVersion,
                "unscored-v1",
                StringComparison.Ordinal) ||
            !string.Equals(
                storedEvidence,
                "No mapping evidence recorded.",
                StringComparison.Ordinal) ||
            (evidenceFingerprint.Length > 0 &&
             evidenceFingerprint.Any(static value => value != '0')))
        {
            return binding;
        }

        ProjectMappingReviewOrigin origin = binding.IsReviewed
            ? ProjectMappingReviewOrigin.Explicit
            : ProjectMappingReviewOrigin.None;
        string scorer = binding.IsReviewed
            ? LegacyExplicitReviewScorer
            : LegacyReconstructionScorer;
        string evidence = binding.IsReviewed
            ? "Legacy schema-1 row preserved as an explicit author review."
            : $"Legacy schema-1 method '{binding.Method}' reconstructed for display only; the row remains unreviewed.";
        double confidence = binding.IsReviewed
            ? binding.Confidence
            : Math.Min(binding.Confidence, 0.89);
        return binding with
        {
            Confidence = confidence,
            Evidence = evidence,
            ReviewOrigin = origin,
            ScorerVersion = scorer,
            EvidenceFingerprint = CreateEvidenceFingerprint(
                "morph",
                binding.SourceChannel,
                binding.TargetMorph,
                binding.Method,
                confidence,
                evidence,
                scorer),
        };
    }

    private static (double Confidence, string Evidence)
        ReconstructLegacyMappingEvidence(string method)
    {
        string normalized = method.Trim();
        if (normalized.Equals("DescriptorHash", StringComparison.OrdinalIgnoreCase))
        {
            return (1.00, "Legacy schema-1 descriptor identity evidence reconstructed; review state was not inferred.");
        }

        if (normalized.Equals("ExactName", StringComparison.OrdinalIgnoreCase))
        {
            return (1.00, "Legacy schema-1 exact-name evidence reconstructed; review state was not inferred.");
        }

        if (normalized.Equals("NormalizedName", StringComparison.OrdinalIgnoreCase))
        {
            return (0.95, "Legacy schema-1 normalized-name evidence reconstructed without hierarchy corroboration; review is required.");
        }

        if (normalized.Equals("Semantic", StringComparison.OrdinalIgnoreCase))
        {
            return (0.82, "Legacy schema-1 semantic evidence lacks persisted side, topology, and transfer-policy corroboration; review is required.");
        }

        if (normalized.Equals("Structural", StringComparison.OrdinalIgnoreCase))
        {
            return (0.70, "Legacy schema-1 structural evidence reconstructed; structural rows are never auto-reviewed.");
        }

        return (0.0, $"Legacy schema-1 method '{method}' has no authoritative reconstructed score; review is required.");
    }

    private static string CreateLegacySourceIdentityKey(
        ProjectAnimation animation) =>
        string.Join(
            "|",
            animation.SourceAssetId.ToString("N"),
            animation.SourceBinding is null
                ? "unresolved"
                : JsonSerializer.Serialize(
                    animation.SourceBinding,
                    SerializerOptions),
            animation.SourceRigSignature ?? string.Empty,
            animation.MimicAssetId?.ToString("N") ?? string.Empty,
            animation.FacialAnimationSourceBinding is null
                ? string.Empty
                : JsonSerializer.Serialize(
                    animation.FacialAnimationSourceBinding,
                    SerializerOptions),
            animation.FacialSourceAssetId?.ToString("N") ?? string.Empty,
            animation.FacialSourceValueUnit?.ToString() ?? string.Empty,
            animation.FacialTiming is null
                ? string.Empty
                : JsonSerializer.Serialize(
                    animation.FacialTiming,
                    SerializerOptions),
            animation.FrameRate.Numerator.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            animation.FrameRate.Denominator.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            animation.FrameCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

    private static Guid CreateDeterministicGuid(
        params string[] parts)
    {
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("|", parts)));
        Span<byte> guidBytes = digest.AsSpan(0, 16);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);
    }

    private static string CreateEvidenceFingerprint(
        params object[] values) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(string.Join("|", values))))
            .ToLowerInvariant();

    private static void ValidateSchema1AssetReferences(
        DlraProject project,
        IReadOnlyDictionary<Guid, ProjectAssetReference> assets)
    {
        if (project.Assets.IsDefault || project.Animations.IsDefault)
        {
            throw new ProjectFormatException(
                "Schema-1 project collections must be initialized before migration.");
        }

        foreach (ProjectAnimation animation in project.Animations)
        {
            ValidateAnimationAssetReferences(
                animation,
                assets,
                "Schema-1 animation");
        }

        if (project.ModelsWorkspace is { } workspace)
        {
            RequireAsset(
                assets,
                workspace.PackageAssetId,
                "Schema-1 Models workspace model");
        }
    }

    private static void ValidateSchema2References(
        DlraProject project,
        ImmutableArray<ProjectAnimationSource> sources,
        ImmutableArray<ProjectAnimationVariant> variants)
    {
        if (project.Assets.IsDefault || project.Models.IsDefault ||
            sources.IsDefault || variants.IsDefault ||
            project.Animations.IsDefault)
        {
            throw new ProjectFormatException(
                "Schema-2/3 project collections must be initialized before normalization.");
        }

        Dictionary<Guid, ProjectAssetReference> assets;
        Dictionary<Guid, ProjectModelEntry> models;
        Dictionary<Guid, ProjectAnimationSource> sourceById;
        try
        {
            assets = project.Assets.ToDictionary(static asset => asset.Id);
            models = project.Models.ToDictionary(static model => model.Id);
            sourceById = sources.ToDictionary(static source => source.Id);
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException(
                "Schema-2 asset, model, and source identifiers must be unique before normalization.",
                exception);
        }

        foreach (ProjectModelEntry model in project.Models)
        {
            RequireAsset(
                assets,
                model.AssetId,
                $"Schema-2 model '{model.Name}'");
        }

        foreach (ProjectAnimationSource source in sources)
        {
            RequireAsset(
                assets,
                source.SourceAssetId,
                $"Schema-2 animation source '{source.Name}'");
            ValidateBindingAssetReferences(
                source.SourceBinding,
                assets,
                $"Schema-2 animation source '{source.Name}'");
            ValidateBindingAssetReferences(
                source.FacialAnimationSourceBinding,
                assets,
                $"Schema-2 facial animation source '{source.Name}'");
            if (source.MimicAssetId is { } mimicAssetId)
            {
                RequireAsset(
                    assets,
                    mimicAssetId,
                    $"Schema-2 mimic source '{source.Name}'");
            }

            if (source.FacialSourceAssetId is { } facialSourceAssetId)
            {
                RequireAsset(
                    assets,
                    facialSourceAssetId,
                    $"Schema-2 facial source '{source.Name}'");
            }
        }

        foreach (ProjectAnimationVariant variant in variants)
        {
            if (!sourceById.ContainsKey(variant.SourceId))
            {
                throw new ProjectFormatException(
                    $"Schema-2 animation variant '{variant.Name}' refers to unknown source '{variant.SourceId}'.");
            }

            if (variant.TargetModelId == Guid.Empty ||
                !models.ContainsKey(variant.TargetModelId))
            {
                throw new ProjectFormatException(
                    $"Schema-2 animation variant '{variant.Name}' refers to unknown target model '{variant.TargetModelId}'.");
            }
        }

        foreach (ProjectAnimation animation in project.Animations)
        {
            ValidateAnimationAssetReferences(
                animation,
                assets,
                "Schema-2 compatibility animation");
        }
    }

    private static void ValidateAnimationAssetReferences(
        ProjectAnimation animation,
        IReadOnlyDictionary<Guid, ProjectAssetReference> assets,
        string context)
    {
        RequireAsset(
            assets,
            animation.SourceAssetId,
            $"{context} '{animation.Name}' source");
        ValidateBindingAssetReferences(
            animation.SourceBinding,
            assets,
            $"{context} '{animation.Name}' source binding");
        ValidateBindingAssetReferences(
            animation.FacialAnimationSourceBinding,
            assets,
            $"{context} '{animation.Name}' facial binding");
        if (animation.TargetAssetId is { } targetAssetId)
        {
            RequireAsset(
                assets,
                targetAssetId,
                $"{context} '{animation.Name}' target model");
        }

        if (animation.MimicAssetId is { } mimicAssetId)
        {
            RequireAsset(
                assets,
                mimicAssetId,
                $"{context} '{animation.Name}' mimic source");
        }

        if (animation.FacialSourceAssetId is { } facialSourceAssetId)
        {
            RequireAsset(
                assets,
                facialSourceAssetId,
                $"{context} '{animation.Name}' facial source");
        }
    }

    private static void ValidateBindingAssetReferences(
        ProjectAnimationSourceBinding? binding,
        IReadOnlyDictionary<Guid, ProjectAssetReference> assets,
        string context)
    {
        if (binding is null)
        {
            return;
        }

        RequireAsset(assets, binding.AssetId, context);
        if (binding.RetailSourceModelAssetId is { } sourceModelAssetId)
        {
            RequireAsset(
                assets,
                sourceModelAssetId,
                $"{context} model");
        }
    }

    private static ProjectAssetReference RequireAsset(
        IReadOnlyDictionary<Guid, ProjectAssetReference> assets,
        Guid assetId,
        string context)
    {
        if (assetId == Guid.Empty ||
            !assets.TryGetValue(assetId, out ProjectAssetReference? asset))
        {
            throw new ProjectFormatException(
                $"{context} refers to unknown project asset '{assetId}'.");
        }

        return asset;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character => Uri.IsHexDigit(character));

    private static ProjectAnimation NormalizeAnimation(
        ProjectAnimation animation,
        Dictionary<Guid, ProjectAssetReference> assets)
    {
        ProjectAnimation normalized = animation;
        if (normalized.SourceBinding is null &&
            assets.TryGetValue(
                normalized.SourceAssetId,
                out ProjectAssetReference? source) &&
            string.Equals(
                Path.GetExtension(source.RelativePath),
                ".fbx",
                StringComparison.OrdinalIgnoreCase) &&
            normalized.SourceRigSignature is { Length: 64 } sourceSignature &&
            sourceSignature.All(static character => Uri.IsHexDigit(character)))
        {
            normalized = normalized with
            {
                SourceBinding = new ProjectAnimationSourceBinding
                {
                    Kind = AnimationSourceKind.LocalFbx,
                    AssetId = normalized.SourceAssetId,
                    Roles = AnimationSourceRoles.Body,
                    SourceRigSignature = sourceSignature,
                    TimingProvenance = AnimationTimingProvenance.EmbeddedFbx,
                },
            };
        }

        if (normalized.VariantGroupId is null &&
            normalized.SourceBinding is not null)
        {
            normalized = normalized with
            {
                VariantGroupId = AnimationVariantKey.CreateGroupId(
                    normalized,
                    assets),
            };
        }

        return normalized;
    }

    private static void ValidateRequiredRootProperties(
        JsonElement root,
        int schemaVersion)
    {
        string[] required = schemaVersion switch
        {
            1 => RequiredSchema1RootProperties,
            2 => RequiredSchema2RootProperties,
            DlraProject.CurrentSchemaVersion =>
                RequiredSchema3RootProperties,
            _ => throw new ProjectFormatException(
                $"Project schema {schemaVersion} is not supported by this application."),
        };
        foreach (string propertyName in required)
        {
            if (!root.TryGetProperty(propertyName, out _))
            {
                throw new ProjectFormatException(
                    $"The schema-{schemaVersion} project is missing required property '{propertyName}'.");
            }
        }
    }
}

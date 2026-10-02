using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// Folds a solved rig conformance back into an imported model so the existing
/// DL1 authoring pipeline consumes it unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The output is an ordinary <see cref="FbxModelAuthoringImportResult"/> whose
/// bone table is the conformed DL1 rig and whose surfaces address it. Nothing
/// downstream needs to know a conformance happened:
/// <c>Dl1CustomModelRigPreparer</c> authors the Chrome +X frames, bounds and
/// physical order exactly as it does for a directly imported rig.
/// </para>
/// <para>
/// The source FBX bytes, materials, textures and mesh parts are carried through
/// untouched, so the package still round-trips to its original input.
/// </para>
/// </remarks>
public sealed record Dl1RigConformanceApplyResult(FbxModelAuthoringImportResult Model, ImmutableArray<int> FitToEffective, ImmutableArray<int> SourceToEffective);

public static class Dl1RigConformanceApplier
{
    /// <param name="settings">
    /// The authored decisions that produced <paramref name="fit"/>. Recorded on
    /// the document so the conformance can be reopened and adjusted later; the
    /// conformed bone table itself is never persisted as a layer.
    /// </param>
    public static FbxModelAuthoringImportResult Apply(
        FbxModelAuthoringImportResult model,
        RigConformanceResult fit,
        CustomModelRigConformance? settings = null,
        CancellationToken cancellationToken = default) => ApplyDetailed(model, fit, settings, cancellationToken).Model;

    public static Dl1RigConformanceApplyResult ApplyDetailed(FbxModelAuthoringImportResult model, RigConformanceResult fit,
        CustomModelRigConformance? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(fit);
        RigDefinition sourceRig = model.Rig ?? throw new InvalidOperationException(
            "A static model has no rig to conform.");

        // A previous output can retain a generated, unweighted source-name
        // helper whose name also appears in the DL1 template. The fitter may
        // classify that row as deforming, but only rows with explicit generated
        // identity provenance, no actual skin weights, and the same fitted
        // parent may be folded into the template row.
        (fit, int[] originalToFit) = MergeTemplateNamedNonDeformExtras(model, fit);

        // Carry the mesh into the conformed skeleton's rest pose first. The
        // transforms are indexed by source bone, so this must happen before the
        // weight transfer rewrites the bindings.
        Dl1RestPoseBakeResult baked = Dl1RestPoseBaker.Bake(
            model.Surfaces,
            fit.RestPoseTransfer,
            cancellationToken);
        var original = model.Package.Document;
        Dl1ConformanceHierarchy hierarchy = ProjectHierarchy(original, fit, cancellationToken);
        Dl1SkinConformanceResult skin = Dl1SkinWeightConformer.ConformToHierarchy(baked.Surfaces, sourceRig,
            hierarchy.SourceToEffective, hierarchy.EffectiveGlobals.Length, cancellationToken);
        var document = original with
        {
            Bones = hierarchy.Bones, AuthoredHelpers = hierarchy.Helpers,
            RigConformance = settings ?? original.RigConformance, LastBuildReceipt = null,
        };
        var effective = document.CreateEffectiveBones();
        var companionTransfer = Dl1CompanionReferenceTransfer.Apply(original.SecondaryMotion,
            original.CreateEffectiveBones(), effective, hierarchy.SourceToEffective,
            fit.RestPoseTransfer.SkinningTransforms, cancellationToken);
        document = document with { SecondaryMotion = companionTransfer.Definition };
        var sourceNames = original.CreateEffectiveBones().ToDictionary(b => b.Name, b => b.Index, StringComparer.Ordinal);
        string? RemapName(string? name) => name is not null && sourceNames.TryGetValue(name, out int index) && hierarchy.SourceToEffective[index] >= 0
            ? effective[hierarchy.SourceToEffective[index]].Name : name;
        var camera = original.Camera with { ActivePreviewNodeName = RemapName(original.Camera.ActivePreviewNodeName) };
        document = document with { AnimationClips = original.AnimationClips.Select(c => c with { RootBoneName = RemapName(c.RootBoneName) }).ToImmutableArray() };
        document = document with { Camera = ResolveCamera(camera, document.CreateEffectiveBones()),
            RigSignature = CustomModelContractSignatures.ComputeRig(document.CreateEffectiveBones()) };
        document = Dl1ConformanceSessionTransfer.Apply(original, document, hierarchy, fit.TemplateId);
        var clips = FbxAnimationTrackReindexer.ReindexAvailable(model.AnimationClips, hierarchy.SourceToEffective, cancellationToken);
        var notes = hierarchy.Notes.AddRange(companionTransfer.Notes).Add("Conformance retained authored helper identities and affine frames. Existing source animation keys are reindexed, not retargeted; review motion or derive a new clip before acceptance.");
        if (clips.Count != model.AnimationClips.Count) notes = notes.Add("Source clips referencing deliberately dropped nodes remain in the embedded source but are unavailable on this rig until derived again.");
        document = document with { Diagnostics = document.Diagnostics.Where(d => d.Code != "conformance_preservation_review")
            .Append(new CustomModelImportDiagnostic { Code = "conformance_preservation_review", Severity = CustomModelImportSeverity.Warning, Message = string.Join(" ", notes) }).ToImmutableArray() };
        document.Validate();
        var inverse = hierarchy.EffectiveGlobals.Select(m => m.InvertedAffine()).ToArray();
        var boundSurfaces = skin.Surfaces.Select(surface => surface with
            { InverseBindMatrices = surface.PaletteBoneIndices.Select(index => inverse[index]).ToImmutableArray() }).ToImmutableArray();
        var candidate = model with
        {
            Package = model.Package with { Document = document }, Rig = document.CreateRigDefinition(),
            Surfaces = boundSurfaces, AnimationClips = clips,
        };
        candidate = Dl1ConformancePreparedHelpers.Preserve(model, candidate, fit, cancellationToken);
        // Prepared-parent policies may adjust authored helper locals. Transport only
        // preview offsets again, from the original positions, after those adjustments.
        // Native name migration has already completed and its source lineage stays intact.
        if (!original.SecondaryMotion.Groups.IsEmpty &&
            !candidate.Package.Document.AuthoredHelpers.SequenceEqual(document.AuthoredHelpers))
        {
            var previewTransfer = Dl1CompanionReferenceTransfer.Apply(original.SecondaryMotion with { NativeSources = [] },
                original.CreateEffectiveBones(), candidate.Package.Document.CreateEffectiveBones(),
                hierarchy.SourceToEffective, fit.RestPoseTransfer.SkinningTransforms, cancellationToken);
            candidate = candidate with { Package = candidate.Package with { Document = candidate.Package.Document with
                { SecondaryMotion = companionTransfer.Definition with { Groups = previewTransfer.Definition.Groups } } } };
        }
        candidate.Package.Document.Validate();
        if (original.RiggingSession is { } oldSession && candidate.Package.Document.RiggingSession is { } changedSession)
            candidate = candidate with { Package = candidate.Package with { Document = candidate.Package.Document with
                { RiggingSession = RiggingSessions.Change(oldSession, changedSession, RiggingEditKind.Anatomy) } } };
        if (candidate.Rig is { } outputRig &&
            candidate.Package.Document.RigConformance is { } conformance)
        {
            // Source-space role choices remain in the record for later source
            // reimport. Mark the exact output rig so the wizard can recognize
            // it without mistaking any stale source rig for an applied result.
            candidate = candidate with
            {
                Package = candidate.Package with
                {
                    Document = candidate.Package.Document with
                    {
                        RigConformance = conformance with
                        {
                            AppliedOutputRigSignature = RigSignature.Compute(outputRig),
                        },
                    },
                },
            };
        }
        candidate.Package.Document.Validate();
        FbxProfileEditGuard.RequireAllowed(model, candidate, cancellationToken);
        var fitToEffective = new int[originalToFit.Length];
        for (int originalIndex = 0; originalIndex < originalToFit.Length; originalIndex++)
        {
            int fitIndex = originalToFit[originalIndex];
            fitToEffective[originalIndex] = fitIndex >= 0 && fitIndex < hierarchy.FitToEffective.Length
                ? hierarchy.FitToEffective[fitIndex]
                : -1;
        }
        return new(candidate, fitToEffective.ToImmutableArray(), hierarchy.SourceToEffective);
    }

    private static (RigConformanceResult Fit, int[] OriginalToFit) MergeTemplateNamedNonDeformExtras(
        FbxModelAuthoringImportResult model,
        RigConformanceResult fit)
    {
        var bones = fit.Bones.ToArray();
        var removals = new HashSet<int>();
        var remap = Enumerable.Range(0, bones.Length).ToArray();
        var templateRowsByName = bones
            .Select((bone, index) => (bone, index))
            .Where(static row => row.bone.TemplateIndex >= 0 && row.bone.SourceBoneIndex < 0)
            .GroupBy(static row => row.bone.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().index, StringComparer.OrdinalIgnoreCase);

        for (int extraIndex = 0; extraIndex < bones.Length; extraIndex++)
        {
            RigConformedBone extra = bones[extraIndex];
            bool previouslyGeneratedUnweightedHelper =
                (extra.IsDeform || extra.Kind == BoneKind.Deform) &&
                IsPreviouslyGeneratedUnweightedHelper(model, extra.SourceBoneIndex);
            if (extra.TemplateIndex >= 0 ||
                (extra.IsDeform || extra.Kind == BoneKind.Deform) &&
                    !previouslyGeneratedUnweightedHelper ||
                !templateRowsByName.TryGetValue(extra.Name, out int templateIndex))
            {
                continue;
            }

            // Preserve a real source row when folding a helper onto its
            // same-name template row. Source-less duplicate rows have no stable
            // source identity to retain, and still need one output identity.
            RigConformedBone target = bones[templateIndex];
            if (target.SourceBoneIndex >= 0 && extra.SourceBoneIndex >= 0 &&
                target.SourceBoneIndex != extra.SourceBoneIndex)
            {
                // Two distinct source entities cannot share one stable identity.
                continue;
            }

            if (previouslyGeneratedUnweightedHelper)
            {
                int targetParent = ResolveRetainedParentIndex(
                    target.ParentIndex,
                    removals,
                    remap);
                int extraParent = ResolveRetainedParentIndex(
                    extra.ParentIndex,
                    removals,
                    remap);
                if (targetParent < -1 || extraParent < -1 ||
                    targetParent != extraParent)
                {
                    // Same-name rows with different or unresolved retained
                    // parents are not proven aliases. A generated parent may
                    // already have been folded into its template counterpart.
                    continue;
                }
            }

            // If the template row is inside the helper branch, folding the
            // ancestor would create a cycle; leave that case for an explicit
            // authoring decision instead.
            bool templateIsDescendant = false;
            for (int ancestor = templateIndex; ancestor >= 0; ancestor = bones[ancestor].ParentIndex)
            {
                if (ancestor == extraIndex)
                {
                    templateIsDescendant = true;
                    break;
                }
            }
            if (templateIsDescendant)
            {
                continue;
            }

            int retainedSource = target.SourceBoneIndex >= 0 ? target.SourceBoneIndex : extra.SourceBoneIndex;
            bones[templateIndex] = target with
            {
                SourceBoneIndex = retainedSource,
                Disposition = retainedSource >= 0 ? RigBoneDisposition.Mapped : target.Disposition,
                Position = previouslyGeneratedUnweightedHelper ? extra.Position : target.Position,
                Orientation = previouslyGeneratedUnweightedHelper ? extra.Orientation : target.Orientation,
                OffsetFromSourceJoint = previouslyGeneratedUnweightedHelper
                    ? extra.OffsetFromSourceJoint
                    : target.OffsetFromSourceJoint,
                SegmentRatio = previouslyGeneratedUnweightedHelper
                    ? extra.SegmentRatio
                    : target.SegmentRatio,
            };
            removals.Add(extraIndex);
            remap[extraIndex] = templateIndex;
        }

        if (removals.Count == 0)
        {
            return (fit, remap);
        }

        var oldToNew = new int[bones.Length];
        Array.Fill(oldToNew, -1);
        var merged = ImmutableArray.CreateBuilder<RigConformedBone>(bones.Length - removals.Count);
        for (int index = 0; index < bones.Length; index++)
        {
            if (removals.Contains(index))
            {
                continue;
            }

            oldToNew[index] = merged.Count;
            merged.Add(bones[index]);
        }

        for (int index = 0; index < merged.Count; index++)
        {
            RigConformedBone bone = merged[index];
            int parent = bone.ParentIndex;
            while (parent >= 0 && removals.Contains(parent))
            {
                parent = remap[parent];
            }
            merged[index] = bone with
            {
                Index = index,
                ParentIndex = parent < 0 ? -1 : oldToNew[parent],
            };
        }

        for (int index = 0; index < remap.Length; index++)
        {
            int mapped = remap[index];
            while (mapped >= 0 && removals.Contains(mapped))
            {
                mapped = remap[mapped];
            }
            remap[index] = mapped < 0 ? -1 : oldToNew[mapped];
        }

        RigConformanceResult normalized = fit.WithBones(merged);
        return (normalized, remap);
    }

    private static int ResolveRetainedParentIndex(
        int parentIndex,
        HashSet<int> removals,
        int[] remap)
    {
        if (parentIndex < 0 || (uint)parentIndex >= (uint)remap.Length)
        {
            return parentIndex == -1 ? -1 : int.MinValue;
        }

        int remaining = remap.Length;
        while (parentIndex >= 0 && removals.Contains(parentIndex))
        {
            if (remaining-- <= 0)
            {
                return int.MinValue;
            }

            parentIndex = remap[parentIndex];
            if (parentIndex < 0 || (uint)parentIndex >= (uint)remap.Length)
            {
                return int.MinValue;
            }
        }

        return parentIndex;
    }

    private static bool IsPreviouslyGeneratedUnweightedHelper(
        FbxModelAuthoringImportResult model,
        int sourceBoneIndex)
    {
        CustomModelDocument document = model.Package.Document;
        if ((uint)sourceBoneIndex >= (uint)document.Bones.Length ||
            document.RiggingSession is not { } session)
        {
            return false;
        }

        CustomModelBone sourceBone = document.Bones[sourceBoneIndex];
        if (sourceBone.FbxObjectId != 0 ||
            HasSkinInfluence(model.Surfaces, sourceBoneIndex))
        {
            return false;
        }

        ImmutableArray<RigParentObservation> observed =
            RiggingSessions.ObserveSourceHierarchy(document);
        Guid sourceEntityId = observed[sourceBoneIndex].EntityId;
        RigEntityBinding? sourceEntity = session.Recipe.Entities
            .FirstOrDefault(entity =>
                entity.OwnerAssetId == document.ModelId &&
                entity.EntityId == sourceEntityId);
        // A prior output may still classify this bone as deforming even when
        // no surface point uses it. The retained studio entity proves it was
        // generated; actual surface weights, not BoneKind/IsWeighted alone,
        // decide whether the same-name alias can be folded safely.
        return sourceEntity is
        {
            Imported: false,
            SourceEntityId: string sourceIdentity,
        } && sourceIdentity.StartsWith(
            "source-name:",
            StringComparison.Ordinal);
    }

    private static bool HasSkinInfluence(
        IReadOnlyList<FbxModelSurface> surfaces,
        int sourceBoneIndex)
    {
        foreach (FbxModelSurface surface in surfaces)
        {
            if (!surface.IsSkinned)
            {
                continue;
            }

            foreach (FbxModelVertex vertex in surface.Vertices)
            {
                if (vertex.BoneIndices.Length != vertex.BoneWeights.Length)
                {
                    return true;
                }

                for (int influence = 0; influence < vertex.BoneIndices.Length; influence++)
                {
                    double weight = vertex.BoneWeights[influence];
                    if (weight == 0.0)
                    {
                        continue;
                    }
                    if (!double.IsFinite(weight) || weight < 0.0)
                    {
                        return true;
                    }

                    int paletteIndex = vertex.BoneIndices[influence];
                    if ((uint)paletteIndex >= (uint)surface.PaletteBoneIndices.Length)
                    {
                        return true;
                    }
                    if (surface.PaletteBoneIndices[paletteIndex] == sourceBoneIndex)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Builds only the conformed rig, without transferring skin weights.
    /// </summary>
    /// <remarks>
    /// This is the interactive path: while an author is placing joints the mesh
    /// deliberately holds still, so re-binding tens of thousands of vertices on
    /// every adjustment would be wasted work. The result is the same hierarchy
    /// <see cref="Apply"/> emits.
    /// </remarks>
    public static RigDefinition CreateRigDefinition(
        RigConformanceResult fit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fit);
        ImmutableArray<CustomModelBone> bones = BuildBones(fit, cancellationToken);
        var definitions = ImmutableArray.CreateBuilder<BoneDefinition>(bones.Length);
        foreach (CustomModelBone bone in bones)
        {
            definitions.Add(new BoneDefinition(
                bone.Index,
                bone.Name,
                bone.ParentIndex,
                bone.LocalBindTransform,
                bone.Kind));
        }

        return new RigDefinition(
            $"conformance:{fit.TemplateId}",
            "Conformed rig",
            definitions.MoveToImmutable());
    }

    internal static Dl1ConformanceHierarchy ProjectHierarchy(CustomModelDocument document, RigConformanceResult fit, CancellationToken token)
    {
        var projection = Dl1ConformanceHelperTransfer.Build(document, fit, BuildBones(fit, token), token);
        var bones = projection.Bones.ToBuilder();
        for (int source = 0; source < document.Bones.Length; source++)
        {
            int target = projection.SourceToEffective[source];
            if (target >= 0 && target < bones.Count) bones[target] = bones[target] with { FbxObjectId = document.Bones[source].FbxObjectId };
        }
        return projection with { Bones = bones.ToImmutable() };
    }

    private static ImmutableArray<CustomModelBone> BuildBones(
        RigConformanceResult fit,
        CancellationToken cancellationToken)
    {
        var globals = new TransformMatrix[fit.Bones.Length];
        var bones = ImmutableArray.CreateBuilder<CustomModelBone>(fit.Bones.Length);
        foreach (RigConformedBone bone in fit.Bones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Source global frames carry inherited scale, and retail template
            // frames carry float drift, so a raw parent-inverse-times-child
            // would shear and refuse to decompose. Project each reference frame
            // onto the nearest rotation first, using the same polar iteration
            // the Chrome frame authoring already relies on.
            TransformMatrix global = WithTranslation(
                Dl1CustomModelRigPreparer.OrthonormalizeRotation(bone.Orientation),
                bone.Position);
            globals[bone.Index] = global;
            TransformMatrix local = bone.ParentIndex < 0
                ? global
                : globals[bone.ParentIndex].InvertedAffine() * global;

            bones.Add(new CustomModelBone
            {
                Index = bone.Index,
                FbxObjectId = 0,
                Name = bone.Name,
                ParentIndex = bone.ParentIndex,
                LocalBindTransform = Decompose(local, bone.Name),
                ExactLocalBindMatrix = local,
                Kind = bone.Kind,
                IsWeighted = bone.IsDeform,
            });
        }

        return bones.MoveToImmutable();
    }

    /// <summary>
    /// Keeps a previously selected preview camera only when the conformed rig
    /// still contains that exact node, so a stale selection cannot survive as a
    /// dangling reference.
    /// </summary>
    private static CustomModelCameraMetadata ResolveCamera(
        CustomModelCameraMetadata camera,
        ImmutableArray<CustomModelBone> bones)
    {
        if (camera.ActivePreviewNodeName is not { } name)
        {
            return camera;
        }

        bool present = bones.Any(bone =>
            string.Equals(bone.Name, name, StringComparison.OrdinalIgnoreCase));
        return present ? camera : camera with { ActivePreviewNodeName = null };
    }

    private static TransformTRS Decompose(TransformMatrix local, string boneName)
    {
        try
        {
            return local.Decompose(1e-7);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                $"Conformed bone '{boneName}' produced a local bind that cannot be decomposed: {exception.Message}",
                exception);
        }
    }

    private static TransformMatrix WithTranslation(
        TransformMatrix rotation,
        Vector3D translation) =>
        new(
            rotation.M11, rotation.M12, rotation.M13, translation.X,
            rotation.M21, rotation.M22, rotation.M23, translation.Y,
            rotation.M31, rotation.M32, rotation.M33, translation.Z,
            0.0, 0.0, 0.0, 1.0);
}

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
        return new(candidate, hierarchy.FitToEffective, hierarchy.SourceToEffective);
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

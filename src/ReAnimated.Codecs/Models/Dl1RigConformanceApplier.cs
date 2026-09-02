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
        CancellationToken cancellationToken = default)
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
        Dl1SkinConformanceResult skin = Dl1SkinWeightConformer.Conform(
            baked.Surfaces,
            sourceRig,
            fit,
            cancellationToken);

        ImmutableArray<CustomModelBone> bones = BuildBones(fit, cancellationToken);
        CustomModelDocument document = model.Package.Document with
        {
            Bones = bones,
            // Conformance replaces the whole imported bone table, so any helper
            // authored against the old row indexes would now point elsewhere.
            AuthoredHelpers = [],
            Camera = ResolveCamera(model.Package.Document.Camera, bones),
            RigSignature = CustomModelContractSignatures.ComputeRig(bones),
            RigConformance = settings ?? model.Package.Document.RigConformance,
            LastBuildReceipt = null,
        };
        document.Validate();

        return model with
        {
            Package = model.Package with { Document = document },
            Rig = document.CreateRigDefinition(),
            Surfaces = skin.Surfaces,
        };
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

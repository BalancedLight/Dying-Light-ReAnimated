using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Evaluation;

/// <summary>
/// How the FPP preview camera bone was chosen. Only the two EyeCamera bindings
/// satisfy the DL1 export contract; the remaining bindings are editor-only
/// preview anchors.
/// </summary>
public enum Dl1FppCameraBinding
{
    None,
    ExactEyeCamera,
    SemanticEyeCamera,
    ExplicitPreviewBone,
    HeadFallback,
    NeckFallback,
}

/// <summary>
/// The outcome of resolving an FPP preview camera bone against a rig.
/// <see cref="Evidence"/> is written for direct display in preview stage
/// messages, so it always explains what was searched and why.
/// </summary>
public sealed record Dl1FppCameraResolution(
    int BoneIndex,
    string? BoneName,
    Dl1FppCameraBinding Binding,
    bool IsEyeCameraContract,
    string Evidence)
{
    public bool IsResolved => BoneIndex >= 0;
}

/// <summary>
/// Chooses the rig bone that anchors the editor FPP preview view.
/// </summary>
/// <remarks>
/// A retail DL1 rig usually carries an EyeCamera helper, but many do not, and
/// nothing forces a decoded hierarchy to name it exactly. Previewing therefore
/// falls back through progressively weaker anatomical evidence instead of
/// failing closed. The EyeCamera name and <c>camera.eye</c> role remain the
/// only bindings that claim the DL1 export contract: every weaker binding
/// reports <see cref="Dl1FppCameraResolution.IsEyeCameraContract"/> as false so
/// no caller can mistake an editor anchor for an exportable helper.
/// Ambiguity is never resolved by guessing an index; it degrades to the next
/// tier and is recorded in the evidence.
/// </remarks>
public static class Dl1FppCameraResolver
{
    private const string HeadRole = "body.head";

    private const string UpperNeckRole = "body.neck.1";

    private const string LowerNeckRole = "body.neck.0";

    public static Dl1FppCameraResolution Resolve(
        RigDefinition rig,
        string? requestedBoneName)
    {
        ArgumentNullException.ThrowIfNull(rig);

        var evidence = ImmutableArray.CreateBuilder<string>();

        // 1. An explicit per-model preview bone always wins. A stale or
        //    ambiguous request degrades to auto-detection instead of blanking
        //    the preview, because the rig may simply have changed underneath a
        //    saved project.
        bool requestsEyeCameraByName = string.Equals(
            requestedBoneName,
            Dl1PreviewContract.EyeCameraBoneName,
            StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(requestedBoneName) &&
            !requestsEyeCameraByName)
        {
            int requestedCount = rig.GetBoneIndices(requestedBoneName).Length;
            int requestedIndex = rig.GetBoneIndex(requestedBoneName);
            if (requestedIndex >= 0)
            {
                BoneDefinition requested = rig.Bones[requestedIndex];
                return IsEyeCameraContractBone(requested)
                    ? new(
                        requestedIndex,
                        requested.Name,
                        Dl1FppCameraBinding.ExactEyeCamera,
                        true,
                        $"The selected preview bone '{requested.Name}' is the EyeCamera contract helper.")
                    : new(
                        requestedIndex,
                        requested.Name,
                        Dl1FppCameraBinding.ExplicitPreviewBone,
                        false,
                        $"Using the preview bone '{requested.Name}' selected for this target model.");
            }

            evidence.Add(
                requestedCount > 1
                    ? $"the selected preview bone '{requestedBoneName}' is ambiguous ({requestedCount} bones share that name)"
                    : $"the selected preview bone '{requestedBoneName}' is not present in rig '{rig.Id}'");
        }

        // 2. The canonical name is checked before the semantic role: a rig may
        //    repeat the camera.eye role on helper duplicates while still
        //    carrying exactly one bone named EyeCamera, and that rig must
        //    preview rather than fail.
        int exactIndex = rig.GetBoneIndex(Dl1PreviewContract.EyeCameraBoneName);
        if (exactIndex >= 0)
        {
            return new(
                exactIndex,
                rig.Bones[exactIndex].Name,
                Dl1FppCameraBinding.ExactEyeCamera,
                true,
                Compose(
                    evidence,
                    $"Bound to the '{Dl1PreviewContract.EyeCameraBoneName}' helper."));
        }

        int eyeCameraNameCount =
            rig.GetBoneIndices(Dl1PreviewContract.EyeCameraBoneName).Length;
        evidence.Add(
            eyeCameraNameCount > 1
                ? $"'{Dl1PreviewContract.EyeCameraBoneName}' is ambiguous ({eyeCameraNameCount} bones share that name)"
                : $"no bone is named '{Dl1PreviewContract.EyeCameraBoneName}'");

        // 3. A uniquely declared camera.eye role is still the export contract.
        if (TryResolveDeclaredRole(
                rig,
                Dl1PreviewContract.EyeCameraSemanticRole,
                out int semanticIndex,
                out int semanticCount))
        {
            return new(
                semanticIndex,
                rig.Bones[semanticIndex].Name,
                Dl1FppCameraBinding.SemanticEyeCamera,
                true,
                Compose(
                    evidence,
                    $"Bound to '{rig.Bones[semanticIndex].Name}', the only bone declaring the '{Dl1PreviewContract.EyeCameraSemanticRole}' role."));
        }

        evidence.Add(
            semanticCount > 1
                ? $"{semanticCount} bones declare the '{Dl1PreviewContract.EyeCameraSemanticRole}' role, so none can be chosen unambiguously"
                : $"no bone declares the '{Dl1PreviewContract.EyeCameraSemanticRole}' role");

        // 4-5. Anatomical fallbacks. These are editor preview anchors only and
        //      deliberately never report the export contract.
        Dl1FppCameraResolution? anatomical =
            ResolveAnatomicalFallback(
                rig,
                HeadRole,
                Dl1FppCameraBinding.HeadFallback,
                "head",
                evidence) ??
            ResolveAnatomicalFallback(
                rig,
                UpperNeckRole,
                Dl1FppCameraBinding.NeckFallback,
                "upper neck",
                evidence) ??
            ResolveAnatomicalFallback(
                rig,
                LowerNeckRole,
                Dl1FppCameraBinding.NeckFallback,
                "neck",
                evidence);
        if (anatomical is not null)
        {
            return anatomical;
        }

        return new(
            -1,
            null,
            Dl1FppCameraBinding.None,
            false,
            Compose(
                evidence,
                $"Rig '{rig.Id}' provides no usable FPP preview camera bone."));
    }

    private static Dl1FppCameraResolution? ResolveAnatomicalFallback(
        RigDefinition rig,
        string role,
        Dl1FppCameraBinding binding,
        string description,
        ImmutableArray<string>.Builder evidence)
    {
        if (TryResolveDeclaredRole(rig, role, out int declaredIndex, out _))
        {
            return new(
                declaredIndex,
                rig.Bones[declaredIndex].Name,
                binding,
                false,
                Compose(
                    evidence,
                    $"Falling back to the {description} bone '{rig.Bones[declaredIndex].Name}', which declares the '{role}' role."));
        }

        // The name classifier is a suggestion engine, so it is consulted only
        // after declared roles and only when it yields a single candidate.
        if (TryResolveClassifiedRole(rig, role, out int classifiedIndex))
        {
            return new(
                classifiedIndex,
                rig.Bones[classifiedIndex].Name,
                binding,
                false,
                Compose(
                    evidence,
                    $"Falling back to the {description} bone '{rig.Bones[classifiedIndex].Name}', identified by name."));
        }

        return null;
    }

    private static bool TryResolveDeclaredRole(
        RigDefinition rig,
        string role,
        out int index,
        out int matchCount) =>
        TryResolveUnique(
            rig,
            bone => string.Equals(
                bone.SemanticRole,
                role,
                StringComparison.OrdinalIgnoreCase),
            out index,
            out matchCount);

    private static bool TryResolveClassifiedRole(
        RigDefinition rig,
        string role,
        out int index) =>
        TryResolveUnique(
            rig,
            bone => bone.SemanticRole is null &&
                string.Equals(
                    HumanoidBoneSemanticClassifier.Classify(bone.Name)?.Role,
                    role,
                    StringComparison.Ordinal),
            out index,
            out _);

    private static bool TryResolveUnique(
        RigDefinition rig,
        Func<BoneDefinition, bool> predicate,
        out int index,
        out int matchCount)
    {
        index = -1;
        matchCount = 0;
        foreach (BoneDefinition bone in rig.Bones)
        {
            if (!predicate(bone))
            {
                continue;
            }

            matchCount++;
            if (matchCount > 1)
            {
                index = -1;
                continue;
            }

            index = bone.Index;
        }

        return matchCount == 1;
    }

    private static bool IsEyeCameraContractBone(BoneDefinition bone) =>
        string.Equals(
            bone.Name,
            Dl1PreviewContract.EyeCameraBoneName,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            bone.SemanticRole,
            Dl1PreviewContract.EyeCameraSemanticRole,
            StringComparison.OrdinalIgnoreCase);

    private static string Compose(
        ImmutableArray<string>.Builder evidence,
        string outcome) =>
        evidence.Count == 0
            ? outcome
            : $"{outcome} Searched first: {string.Join("; ", evidence)}.";
}

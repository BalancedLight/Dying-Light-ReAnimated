using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>
/// Pure document operations for authored helper nodes. Imported FBX bones are
/// never rewritten; all user-created rows remain in the schema-2 helper layer.
/// Callers can wrap these immutable transitions in their normal undo service.
/// </summary>
public static class CustomModelHelperAuthoring
{
    public const string GameCameraName = "EyeCamera";

    public static CustomModelDocument DuplicateAsHelper(
        CustomModelDocument document,
        int sourceNodeIndex,
        CustomModelAuthoredHelperKind kind,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ImmutableArray<CustomModelBone> effective = document.CreateEffectiveBones();
        if ((uint)sourceNodeIndex >= (uint)effective.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceNodeIndex));
        }

        string requestedName = string.IsNullOrWhiteSpace(name)
            ? BuildUniqueName(document, $"{effective[sourceNodeIndex].Name}_{kind}")
            : name.Trim();
        EnsureUniqueName(document, requestedName);
        var helper = new CustomModelAuthoredHelper
        {
            Name = requestedName,
            ParentNodeIndex = sourceNodeIndex,
            // A child identity transform begins at its selected parent's global
            // bind transform and provides a clean local offset for editing.
            LocalTransform = TransformTRS.Identity,
            ExactLocalMatrix = TransformMatrix.Identity,
            Kind = kind,
        };
        return FinalizeRigMutation(document with
        {
            AuthoredHelpers = document.AuthoredHelpers.Add(helper),
        });
    }

    public static CustomModelDocument CreateEyeCameraHelper(
        CustomModelDocument document,
        int sourceNodeIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        ImmutableArray<CustomModelBone> effective = document.CreateEffectiveBones();
        CustomModelBone? existing = effective.FirstOrDefault(static bone =>
            string.Equals(bone.Name, GameCameraName, StringComparison.Ordinal));
        if (existing is not null)
        {
            string resolution = existing.Kind == BoneKind.Camera && !existing.IsWeighted
                ? "Choose the existing helper explicitly or cancel camera promotion."
                : "The name is owned by a node that is not an unweighted camera helper.";
            throw new InvalidOperationException(
                $"The exact EyeCamera name already exists. {resolution}");
        }

        if (effective.Any(static bone =>
                string.Equals(bone.Name, GameCameraName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "A case-insensitive EyeCamera name collision must be resolved before creating the game camera helper.");
        }

        CustomModelDocument updated = DuplicateAsHelper(
            document,
            sourceNodeIndex,
            CustomModelAuthoredHelperKind.Camera,
            GameCameraName);
        return SelectPreviewCamera(updated, GameCameraName);
    }

    public static CustomModelDocument SetLocalTransform(
        CustomModelDocument document,
        Guid helperId,
        TransformTRS localTransform,
        TransformMatrix? exactLocalMatrix = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (helperId == Guid.Empty)
        {
            throw new ArgumentException("The helper identifier cannot be empty.", nameof(helperId));
        }

        if (!localTransform.IsFinite)
        {
            throw new ArgumentException("The helper transform must be finite.", nameof(localTransform));
        }

        int index = -1;
        for (int candidate = 0; candidate < document.AuthoredHelpers.Length; candidate++)
        {
            if (document.AuthoredHelpers[candidate].Id == helperId)
            {
                index = candidate;
                break;
            }
        }
        if (index < 0)
        {
            throw new KeyNotFoundException($"Authored helper '{helperId}' was not found.");
        }

        TransformMatrix exact = exactLocalMatrix ?? localTransform.ToMatrix();
        CustomModelAuthoredHelper replacement = document.AuthoredHelpers[index] with
        {
            LocalTransform = localTransform,
            ExactLocalMatrix = exact,
        };
        return FinalizeRigMutation(document with
        {
            AuthoredHelpers = document.AuthoredHelpers.SetItem(index, replacement),
        });
    }

    public static CustomModelDocument SelectPreviewCamera(
        CustomModelDocument document,
        string? nodeName)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (nodeName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
            string[] matches = document.CreateEffectiveBones()
                .Where(bone => string.Equals(bone.Name, nodeName, StringComparison.OrdinalIgnoreCase))
                .Select(static bone => bone.Name)
                .ToArray();
            if (matches.Length != 1)
            {
                throw new KeyNotFoundException(
                    $"Preview camera node '{nodeName}' is missing or ambiguous.");
            }

            nodeName = matches[0];
        }

        CustomModelDocument updated = document with
        {
            Camera = document.Camera with { ActivePreviewNodeName = nodeName },
        };
        updated.Validate();
        return updated;
    }

    private static CustomModelDocument FinalizeRigMutation(CustomModelDocument document)
    {
        string rigSignature = CustomModelContractSignatures.ComputeRig(
            document.CreateEffectiveBones());
        CustomModelDocument updated = document with
        {
            RigSignature = rigSignature,
            LastBuildReceipt = null,
        };
        updated.Validate();
        return updated;
    }

    private static string BuildUniqueName(CustomModelDocument document, string stem)
    {
        string candidate = stem;
        int suffix = 2;
        while (document.CreateEffectiveBones().Any(bone =>
                   string.Equals(bone.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{stem}_{suffix++}";
        }

        return candidate;
    }

    private static void EnsureUniqueName(CustomModelDocument document, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (document.CreateEffectiveBones().Any(bone =>
                string.Equals(bone.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Hierarchy name '{name}' is already in use.",
                nameof(name));
        }
    }
}

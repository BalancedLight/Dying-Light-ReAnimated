using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1GripHelperFitResult(
    FbxModelAuthoringImportResult Model,
    ImmutableArray<string> ChangedHelpers,
    ImmutableArray<string> Messages);

/// <summary>Places the two hand sockets from the current fitted hand frames.</summary>
public static class Dl1GripHelperFitter
{
    public static bool IsHandGrip(string name) =>
        name.Equals("l_handholder", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("r_handholder", StringComparison.OrdinalIgnoreCase);

    public static string DisplayName(string name) =>
        name.Equals("l_handholder", StringComparison.OrdinalIgnoreCase)
            ? "Left hand" : name.Equals("r_handholder", StringComparison.OrdinalIgnoreCase)
                ? "Right hand" : name;

    public static Dl1GripHelperFitResult Apply(
        FbxModelAuthoringImportResult model,
        Dl1RigTemplate reference,
        bool recalibrateManualPlacements = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(reference);
        CustomModelDocument original = model.Package.Document;
        CustomModelDocument document = original.RiggingSession is null
            ? original with { RiggingSession = RiggingSessions.Create(original, RigStudioEntryPath.AdaptExistingRig) }
            : original;
        var bones = document.CreateEffectiveBones();
        var globals = new TransformMatrix[bones.Length];
        for (int index = 0; index < bones.Length; index++)
            globals[index] = bones[index].ParentIndex < 0 ? bones[index].ExactLocalBindMatrix
                : globals[bones[index].ParentIndex] * bones[index].ExactLocalBindMatrix;
        var observed = RiggingSessions.ObserveSourceHierarchy(document);
        Dictionary<string, Vector3D> currentRoles = new(StringComparer.Ordinal);
        foreach (Dl1RigTemplateEntity target in reference.Entities)
        {
            string? role = target.SemanticRole ?? HumanoidBoneSemanticClassifier.Classify(target.Name)?.Role;
            if (role is null) continue;
            int[] matching = Enumerable.Range(0, bones.Length).Where(i =>
                bones[i].Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length == 1) currentRoles.TryAdd(role, globals[matching[0]].Translation);
        }
        var indicesById = observed.Select((node, index) => (node.EntityId, index))
            .ToDictionary(pair => pair.EntityId, pair => pair.index);
        foreach (RigRoleAssignment assignment in document.RiggingSession!.Recipe.Assignments)
            if (indicesById.TryGetValue(assignment.EntityId, out int index))
                currentRoles.TryAdd(assignment.RoleId, globals[index].Translation);
        foreach (var role in RolePositions(bones.Select(b =>
            (b.Name, HumanoidBoneSemanticClassifier.Classify(b.Name)?.Role, globals[b.Index].Translation))))
            currentRoles.TryAdd(role.Key, role.Value);
        Dictionary<string, Vector3D> referenceRoles = RolePositions(reference.Entities.Select(b =>
            (b.Name, b.SemanticRole ?? HumanoidBoneSemanticClassifier.Classify(b.Name)?.Role, b.GlobalRestMatrix.Translation)));
        var changed = ImmutableArray.CreateBuilder<string>();
        var messages = ImmutableArray.CreateBuilder<string>();

        foreach ((string name, RigHandSide side) in new[]
            { ("l_handholder", RigHandSide.Left), ("r_handholder", RigHandSide.Right) })
        {
            int index = Enumerable.Range(0, bones.Length).FirstOrDefault(i =>
                bones[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase), -1);
            Dl1RigTemplateEntity? target = reference.Entities.FirstOrDefault(b =>
                b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || target is null) continue;
            int parent = bones[index].ParentIndex;
            if (parent < 0 || parent >= document.Bones.Length)
            {
                messages.Add($"{DisplayName(name)} needs a direct hand parent.");
                continue;
            }
            try
            {
                RigGripPlacementSolution placement = RigGripPlacementSolver.Solve(
                    RigPalmLandmarks.FromRolePositions(currentRoles, side),
                    RigPalmLandmarks.FromRolePositions(referenceRoles, side),
                    target.GlobalRestMatrix, globals[parent]);
                Guid helperId = observed[index].EntityId;
                HelperRecipe? saved = document.RiggingSession!.Recipe.Helpers.FirstOrDefault(h => h.EntityId == helperId);
                CustomModelDocument next = RigGripHelperAuthoring.Apply(document,
                    document.RiggingSession.CreateJobToken(),
                    [new(observed[parent].EntityId, helperId, bones[index].Name,
                        $"grip.{side.ToString().ToLowerInvariant()}", placement,
                        saved?.BoundsCenter ?? Vector3D.Zero,
                        saved?.BoundsHalfExtents ?? new Vector3D(.005, .005, .005))],
                    recalibrateManualPlacements);
                if (!ReferenceEquals(next, document)) changed.Add(bones[index].Name);
                document = next;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
            {
                messages.Add($"{DisplayName(name)}: {error.Message}");
            }
        }

        if (changed.Count == 0) return new(model, [], messages.ToImmutable());
        FbxModelAuthoringImportResult result = model with
        {
            Package = model.Package with { Document = document },
            Rig = document.CreateRigDefinition(),
        };
        return new(result, changed.ToImmutable(), messages.ToImmutable());
    }

    private static Dictionary<string, Vector3D> RolePositions(
        IEnumerable<(string Name, string? Role, Vector3D Position)> nodes) => nodes
        .Where(n => n.Role is not null)
        .GroupBy(n => n.Role!, StringComparer.Ordinal)
        .Where(group => group.Count() == 1)
        .ToDictionary(group => group.Key, group => group.Single().Position, StringComparer.Ordinal);
}

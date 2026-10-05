using System.Collections.Immutable;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1RagdollBoneUse(
    string BoneName, string ShapeToken, double RelativeMass, double? RadiusMultiplier,
    int CallIndex)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public double? ScaleMultiplier=>RadiusMultiplier;
}

public sealed record Dl1RagdollJoint(
    string FirstBone, string SecondBone, string JointToken,
    ImmutableArray<double> AxisValues, int CallIndex);

public sealed record Dl1RagdollSetting(
    string SectionName, string Name, ImmutableArray<string> Arguments, int CallIndex);

/// <summary>Measured syntax from the DL1 ragdoll PHX family, not a native physics model.</summary>
public sealed record Dl1RagdollDocument(
    NativeCharacterScriptDocument Syntax,
    ImmutableArray<Dl1RagdollBoneUse> Bones,
    ImmutableArray<Dl1RagdollJoint> Joints,
    ImmutableArray<Dl1RagdollSetting> PhysicsSettings,
    ImmutableArray<Dl1RagdollSetting> RagdollSettings,
    ImmutableArray<Dl1RagdollSetting> CollisionSettings,
    ImmutableArray<Dl1RagdollSetting> SynchronizationSettings,
    ImmutableArray<Dl1RagdollSetting> JointSettings,
    ImmutableArray<NativeCharacterReference> References,
    ImmutableArray<NativeCharacterDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(static diagnostic => !diagnostic.IsError);

    /// <summary>
    /// Rename verified bone tokens, including two-name joint identifiers. Any
    /// remaining unclassified occurrence fails closed rather than dangling.
    /// </summary>
    public NativeCharacterRenameResult RenameBone(
        string oldName, string newName, IEnumerable<string> availableBones)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ArgumentNullException.ThrowIfNull(availableBones);
        var diagnostics = ImmutableArray.CreateBuilder<NativeCharacterDiagnostic>();
        if (!availableBones.Contains(newName, StringComparer.Ordinal))
            diagnostics.Add(new("ragdoll_rename_target_missing", $"Bone '{newName}' is absent from the supplied inventory.", true));
        if (Bones.Any(bone => bone.BoneName == newName && bone.BoneName != oldName))
            diagnostics.Add(new("ragdoll_rename_conflict", $"Bone '{newName}' is already declared.", true));
        var edits = new List<NativeCharacterTokenReplacement>();
        var classified = new HashSet<(int Call, int Argument)>();
        foreach (NativeCharacterReference reference in References.Where(reference =>
                     reference.Kind == NativeCharacterReferenceKind.Bone && reference.Name == oldName))
        {
            edits.Add(new(reference.CallIndex, reference.ArgumentIndex, oldName, newName));
            classified.Add((reference.CallIndex, reference.ArgumentIndex));
        }
        foreach (NativeCharacterReference pair in References.Where(reference =>
                     reference.Kind == NativeCharacterReferenceKind.JointPair))
        {
            string[] names = pair.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (names.Length != 2 || !names.Contains(oldName, StringComparer.Ordinal)) continue;
            string changed = string.Join(' ', names.Select(name => name == oldName ? newName : name));
            edits.Add(new(pair.CallIndex, pair.ArgumentIndex, pair.Name, changed));
            classified.Add((pair.CallIndex, pair.ArgumentIndex));
        }
        if (edits.Count == 0)
            diagnostics.Add(new("ragdoll_rename_source_missing", $"No verified bone token named '{oldName}' exists.", true));
        for (int index = 0; index < Syntax.Calls.Length; index++)
        foreach (NativeCharacterQuotedArgument token in Syntax.Calls[index].QuotedArguments)
        {
            if (!classified.Contains((index, token.ArgumentIndex)) &&
                token.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(oldName, StringComparer.Ordinal))
                diagnostics.Add(new("ragdoll_rename_unclassified_reference",
                    $"Call {index} contains an unclassified '{oldName}' token.", true));
        }
        if (diagnostics.Any(static diagnostic => diagnostic.IsError))
            return new(Syntax, diagnostics.ToImmutable());
        return new(Syntax.ReplaceQuotedArguments(edits), diagnostics.ToImmutable());
    }
}

public static class Dl1RagdollCodec
{
    private static readonly HashSet<string> CollisionNames = [
        "CollisionHelper", "NoCollidedPair", "SelfCollisionGeom", "FloatingGeom", "GeomFrictionMul",
        "FixBone", "CollisionDamageToMeMul", "CollisionDamageToOtherMul", "AllowContinuousCollisions",
    ];
    private static readonly HashSet<string> SynchronizationNames = [
        "RootSynchro", "BoneSynchro", "BoneIk", "SynchroMode", "SynchroOwner",
        "NPBoneSynchroExclusionPropagate", "NPBoneSynchroHidePropagate",
    ];
    private static readonly HashSet<string> PairArgumentNames = [
        "Set1DOFStops", "Set3DOFStops", "SetAnchorPosition", "SetFrictionForce", "Tendon",
    ];

    public static Dl1RagdollDocument Read(
        string text, IEnumerable<string>? availableBones = null,
        IEnumerable<string>? availableResources = null)
    {
        NativeCharacterScriptDocument syntax = NativeCharacterScriptCodec.Parse(text);
        var bones = ImmutableArray.CreateBuilder<Dl1RagdollBoneUse>();
        var joints = ImmutableArray.CreateBuilder<Dl1RagdollJoint>();
        var physics = ImmutableArray.CreateBuilder<Dl1RagdollSetting>();
        var ragdoll = ImmutableArray.CreateBuilder<Dl1RagdollSetting>();
        var collision = ImmutableArray.CreateBuilder<Dl1RagdollSetting>();
        var synchronization = ImmutableArray.CreateBuilder<Dl1RagdollSetting>();
        var jointSettings = ImmutableArray.CreateBuilder<Dl1RagdollSetting>();
        var references = ImmutableArray.CreateBuilder<NativeCharacterReference>();
        var diagnostics = ImmutableArray.CreateBuilder<NativeCharacterDiagnostic>();
        HashSet<string>? knownBones = availableBones?.ToHashSet(StringComparer.Ordinal);
        HashSet<string>? knownResources = availableResources?.ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < syntax.Calls.Length; index++)
        {
            NativeCharacterCall call = syntax.Calls[index];
            string section = call.ParentCallIndex >= 0 ? syntax.Calls[call.ParentCallIndex].Name : string.Empty;
            var setting = new Dl1RagdollSetting(section, call.Name, call.Arguments, index);
            if (section == "PhysicsParams") physics.Add(setting);
            if (section == "RagdollParams") ragdoll.Add(setting);
            if (CollisionNames.Contains(call.Name)) collision.Add(setting);
            if (SynchronizationNames.Contains(call.Name)) synchronization.Add(setting);
            if (section == "Joints") jointSettings.Add(setting);
            try
            {
                switch (call.Name)
                {
                    case "!include":
                        RequireCount(call, 1);
                        AddResource(0, NativeCharacterReferenceKind.IncludeResource);
                        break;
                    case "BehaviorSet":
                        RequireCount(call, 1);
                        AddResource(0, NativeCharacterReferenceKind.PhysicsResource);
                        break;
                    case "UseBone":
                    case "UseBoneScale":
                        RequireCount(call, call.Name == "UseBone" ? 3 : 4);
                        string bone = AddBone(0);
                        string shape = NativeCharacterScriptCodec.Quoted(call.Arguments[1]);
                        double relativeMass = NativeCharacterScriptCodec.FiniteNumber(call.Arguments[2]);
                        double? radius = call.Name == "UseBoneScale"
                            ? NativeCharacterScriptCodec.FiniteNumber(call.Arguments[3]) : null;
                        bones.Add(new(bone, shape, relativeMass, radius, index));
                        break;
                    case "DefineJoint":
                        RequireCount(call, 9);
                        string first = AddBone(0), second = AddBone(1);
                        string jointKind = NativeCharacterScriptCodec.Quoted(call.Arguments[2]);
                        joints.Add(new(first, second, jointKind,
                            call.Arguments.Skip(3).Select(NativeCharacterScriptCodec.FiniteNumber).ToImmutableArray(), index));
                        break;
                    case "CollisionHelper":
                    case "NoCollidedPair":
                    case "RootSynchro":
                    case "BoneSynchro":
                    case "BoneIk":
                        if (call.Arguments.Length < 2) throw new FormatException("Two bone names are required.");
                        AddBone(0); AddBone(1);
                        break;
                    case "SelfCollisionGeom":
                    case "FloatingGeom":
                    case "GeomFrictionMul":
                    case "FixBone":
                    case "NPBoneSynchroExclusionPropagate":
                    case "NPBoneSynchroHidePropagate":
                        if (call.Arguments.Length < 1) throw new FormatException("A bone name is required.");
                        AddBone(0);
                        break;
                    default:
                        if (PairArgumentNames.Contains(call.Name) && call.Arguments.Length > 0)
                        {
                            string pair = NativeCharacterScriptCodec.Quoted(call.Arguments[0]);
                            if (pair.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 2)
                                throw new FormatException("A two-bone joint identifier is required.");
                            references.Add(new(NativeCharacterReferenceKind.JointPair, pair, index, 0));
                        }
                        break;
                }
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
            {
                diagnostics.Add(new("ragdoll_statement_invalid", $"'{call.Name}': {exception.Message}", true));
            }

            string AddBone(int argument)
            {
                string value = NativeCharacterScriptCodec.Quoted(call.Arguments[argument]);
                references.Add(new(NativeCharacterReferenceKind.Bone, value, index, argument));
                if (knownBones is not null && !knownBones.Contains(value))
                    diagnostics.Add(new("ragdoll_bone_missing", $"Bone '{value}' is absent from the supplied hierarchy.", true));
                return value;
            }

            void AddResource(int argument, NativeCharacterReferenceKind kind)
            {
                string value = NativeCharacterScriptCodec.Quoted(call.Arguments[argument]);
                references.Add(new(kind, value, index, argument));
                if (knownResources is not null && !knownResources.Contains(value))
                    diagnostics.Add(new("ragdoll_resource_missing", $"Resource '{value}' is absent from the supplied inventory.", true));
            }
        }
        if (bones.Select(static bone => bone.BoneName).Distinct(StringComparer.Ordinal).Count() != bones.Count)
            diagnostics.Add(new("ragdoll_bone_duplicate", "UseBone/UseBoneScale declares a bone more than once.", true));
        return new(syntax, bones.ToImmutable(), joints.ToImmutable(), physics.ToImmutable(),
            ragdoll.ToImmutable(), collision.ToImmutable(), synchronization.ToImmutable(),
            jointSettings.ToImmutable(), references.ToImmutable(), diagnostics.ToImmutable());
    }

    private static void RequireCount(NativeCharacterCall call, int expected)
    {
        if (call.Arguments.Length != expected)
            throw new FormatException($"Expected {expected} arguments, got {call.Arguments.Length}.");
    }
}

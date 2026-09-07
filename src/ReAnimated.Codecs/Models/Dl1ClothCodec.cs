using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Models;

public sealed record NativeClothDiagnostic(string Code, string Message, bool IsError = false);
public sealed record NativeClothCommand(string Name, ImmutableArray<string> Arguments, int Start, int Length);
public sealed record NativeClothNode(int X, int Y, string BoneName, int Type, double RightDistance, double DownDistance);
public sealed record NativeClothClip(int X, int Y, int TargetX, int TargetY, double RightDistance, double DownDistance);
public sealed record NativeClothCollision(string Command, ImmutableArray<string> Arguments);
public sealed record NativeClothBinding(string ResourceName, int Enabled, int Flag);

/// <summary>A lossless script syntax document. Includes and expressions are retained, never executed.</summary>
public sealed record NativeClothSyntax(string Text, ImmutableArray<NativeClothCommand> Commands)
{
    public string Write() => Text;

    /// <summary>Replace one complete call; all unrelated bytes, comments and unsupported calls survive.</summary>
    public NativeClothSyntax ReplaceCommand(int index, string replacement)
    {
        NativeClothSyntax parsed = Dl1ClothCodec.Parse(replacement);
        if (parsed.Commands.Length != 1 || parsed.Commands[0].Start != 0 || parsed.Commands[0].Length != replacement.Length)
            throw new ArgumentException("Replacement must be one complete native call.", nameof(replacement));
        NativeClothCommand command = Commands[index];
        return Dl1ClothCodec.Parse(Text[..command.Start] + replacement + Text[(command.Start + command.Length)..]);
    }
}

public sealed record NativePhxDocument(
    NativeClothSyntax Syntax,
    int Width,
    int Height,
    ImmutableArray<NativeClothNode> Nodes,
    ImmutableArray<NativeClothClip> Clips,
    ImmutableDictionary<string, ImmutableArray<double>> Parameters,
    ImmutableArray<NativeClothCollision> Collisions,
    ImmutableArray<NativeClothDiagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(d => d.IsError);
}

public sealed record NativeMpClothDocument(NativeClothSyntax Syntax, ImmutableArray<NativeClothBinding> Bindings,
    ImmutableArray<NativeClothDiagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(d => d.IsError);
}

/// <summary>
/// Bounded shipped DL1 PHX/MPCloth syntax reader and lossless writer. Native parameters remain native;
/// this codec does not guess simulation parity, mesh-bound centers, virtual endpoints or seam rules.
/// </summary>
public static class Dl1ClothCodec
{
    private static readonly Dictionary<string, int> ScalarArities = new(StringComparer.Ordinal)
    {
        ["Viscous"] = 1, ["AirFriction"] = 1, ["CheckLengthsStepsPerSec"] = 1,
        ["StructuralStiffness"] = 2, ["ShearStiffness"] = 2, ["FlexionStiffness"] = 2,
        ["Mass"] = 1, ["GravityMul"] = 1, ["WindInfluence"] = 1, ["WindGust"] = 2,
        ["ForceGeneratorsInfluence"] = 1, ["CheckBendingStiffness"] = 1,
        ["CollisionSlide"] = 1, ["CollisionBounce"] = 1, ["CollisionSpeedMod"] = 1,
        ["CollisionNormalAngleThreshold"] = 1, ["CollisionShapesMoveInfluence"] = 1,
        ["CollisionSegmentDivision"] = 1, ["SynchroWithPhysics"] = 1,
        ["HorizontSpringRatio"] = 1, ["ParentMoveMul"] = 1, ["ObjectMoveMul"] = 1,
        ["AnimationMoveMul"] = 1, ["UseFlatNormalCalculations"] = 1,
        ["EnableLengthMulForAll"] = 1, ["EnableCheckingIfHidden"] = 1, ["Mode3D"] = 1,
        ["EnableLoddedSynchro"] = 1, ["StartStabilisation"] = 1, ["UnimportanceThresholds"] = 2,
        ["AutoDisableVel"] = 2, ["EnableAPBIn"] = 1, ["EnableAPBOut"] = 1,
        ["NodeCollisionSphereRadius"] = 1, ["ExternalCollisionAffectMul"] = 1,
        ["ExternalDamageAffectMul"] = 1, ["StandaloneMode"] = 1,
    };

    public static NativePhxDocument ReadPhx(string text, IEnumerable<string>? boneNames = null)
    {
        NativeClothSyntax syntax = Parse(text);
        var diagnostics = ImmutableArray.CreateBuilder<NativeClothDiagnostic>();
        var nodes = ImmutableArray.CreateBuilder<NativeClothNode>();
        var clips = ImmutableArray.CreateBuilder<NativeClothClip>();
        var collisions = ImmutableArray.CreateBuilder<NativeClothCollision>();
        var parameters = ImmutableDictionary.CreateBuilder<string, ImmutableArray<double>>(StringComparer.Ordinal);
        var variables = new Dictionary<string, double>(StringComparer.Ordinal);
        HashSet<string>? inventory = boneNames?.ToHashSet(StringComparer.Ordinal);
        int width = 0, height = 0, headers = 0, grids = 0;
        foreach (NativeClothCommand call in syntax.Commands)
        {
            try
            {
                string name = call.Name;
                ImmutableArray<string> a = call.Arguments;
                if (name == "!include") { Count(a, 1); _ = Quoted(a[0]); continue; }
                if (name.StartsWith('$'))
                {
                    Count(a, 2);
                    if (a[0] is not ("f" or "i")) throw new FormatException("Only numeric script variables are resolved.");
                    variables.Add(name[1..], Number(a[1]));
                    continue;
                }
                if (name == "MeshPartCloth") { Count(a, 0); headers++; continue; }
                if (name == "BonesGridSize") { Count(a, 2); width = Integer(a[0]); height = Integer(a[1]); grids++; continue; }
                if (name == "Bone")
                {
                    Count(a, 6);
                    var node = new NativeClothNode(Integer(a[0]), Integer(a[1]), Quoted(a[2]), Integer(a[3]), Number(a[4]), Number(a[5]));
                    if (node.Type is not (0 or 1))
                        diagnostics.Add(new("native_node_type_unsupported", "Only normal and fixed Bone nodes are resolved; source is retained.", true));
                    if (node.BoneName.Length > 0) Bone(node.BoneName);
                    else diagnostics.Add(new("native_virtual_endpoint", "An empty bone is a native virtual endpoint; preview requires an explicitly authored position."));
                    nodes.Add(node);
                    continue;
                }
                if (name == "Clip")
                {
                    Count(a, 6);
                    clips.Add(new(Integer(a[0]), Integer(a[1]), Integer(a[2]), Integer(a[3]), Number(a[4]), Number(a[5])));
                    continue;
                }
                if (ScalarArities.TryGetValue(name, out int arity))
                {
                    Count(a, arity);
                    if (parameters.ContainsKey(name)) diagnostics.Add(new("native_parameter_repeated", $"Repeated '{name}' retained; the last scalar declaration is shown."));
                    parameters[name] = a.Select(Number).ToImmutableArray();
                    continue;
                }
                if (name is "CollisionSphere" or "CollisionSphereShift" or "CollisionCapsule" or "CollisionCapsuleBetween")
                {
                    Count(a, name switch { "CollisionSphere" => 2, "CollisionCapsuleBetween" => 5, _ => 3 });
                    Bone(Quoted(a[0]));
                    if (name == "CollisionSphereShift") _ = Vector(a[1]);
                    if (name == "CollisionCapsuleBetween")
                    {
                        Bone(Quoted(a[2]));
                        if (Integer(a[1]) is not (0 or 1) || Integer(a[3]) is not (0 or 1)) throw new FormatException("Unknown capsule attachment type.");
                    }
                    else if (name == "CollisionCapsule") _ = Number(a[1]);
                    _ = Number(a[^1]);
                    collisions.Add(new(name, a));
                    // Native spheres/shifted spheres use mesh bounds, not bone origins. Preserve
                    // that distinction instead of manufacturing a false collision preview.
                    if (name != "CollisionCapsuleBetween" || Integer(a[1]) != 0 || Integer(a[3]) != 0)
                        diagnostics.Add(new("native_collision_bounds_required", $"'{name}' requires mesh-bound metadata before native collision placement can be previewed."));
                    continue;
                }
                diagnostics.Add(new("native_statement_unsupported", $"'{name}' is preserved but is not interpreted. Native export readiness requires review."));
            }
            catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
            {
                diagnostics.Add(new("native_statement_invalid", $"'{call.Name}': {exception.Message}", true));
            }
        }
        if (headers != 1 || grids != 1 || width <= 0 || height <= 0 || (long)width * height > 4096)
            diagnostics.Add(new("native_grid_invalid", "PHX requires one header and one bounded positive grid.", true));
        else
        {
            var cells = new HashSet<(int, int)>();
            foreach (NativeClothNode node in nodes)
                if (node.X < 0 || node.Y < 0 || node.X >= width || node.Y >= height || !cells.Add((node.X, node.Y)))
                    diagnostics.Add(new("native_grid_cell_invalid", "Bone cells are duplicated or outside the grid.", true));
            var clipTargets = new Dictionary<(int, int), (int, int)>();
            foreach (NativeClothClip clip in clips)
            {
                if (clip.X < 0 || clip.Y < 0 || clip.X >= width || clip.Y >= height || !cells.Add((clip.X, clip.Y)))
                    diagnostics.Add(new("native_grid_cell_invalid", "Clip cells are duplicated or outside the grid.", true));
                if (clip.TargetX < 0 || clip.TargetY < 0 || clip.TargetX >= width || clip.TargetY >= height ||
                    (clip.X == clip.TargetX && clip.Y == clip.TargetY))
                    diagnostics.Add(new("native_clip_target_invalid", "Clip target is outside the grid or refers to itself.", true));
                if (!clipTargets.TryAdd((clip.X, clip.Y), (clip.TargetX, clip.TargetY)))
                    diagnostics.Add(new("native_clip_target_invalid", "A grid cell has more than one Clip target.", true));
            }
            foreach ((var cell, var target) in clipTargets)
            {
                if (!cells.Contains(target)) diagnostics.Add(new("native_clip_target_missing", "Clip target has no grid declaration.", true));
                var visited = new HashSet<(int, int)> { cell };
                var current = target;
                while (clipTargets.TryGetValue(current, out var next))
                {
                    if (!visited.Add(current)) { diagnostics.Add(new("native_clip_cycle", "Clip target links contain a cycle.", true)); break; }
                    current = next;
                }
            }
            if (cells.Count != width * height) diagnostics.Add(new("native_grid_incomplete", "Not every grid cell has a supported Bone or Clip declaration.", true));
            if (!nodes.Any(n => n.Type == 1)) diagnostics.Add(new("native_anchor_missing", "Grid has no fixed Bone anchor.", true));
        }
        return new(syntax, width, height, nodes.ToImmutable(), clips.ToImmutable(), parameters.ToImmutable(), collisions.ToImmutable(), diagnostics.ToImmutable());

        double Number(string value)
        {
            if (variables.TryGetValue(value, out double variable)) return variable;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                throw new FormatException($"Unresolved finite numeric value '{value}'.");
            return number;
        }
        int Integer(string value)
        {
            double number = Number(value);
            if (number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue) throw new FormatException("Integer required.");
            return (int)number;
        }
        Vector3D Vector(string value)
        {
            if (!value.StartsWith('[') || !value.EndsWith(']')) throw new FormatException("Bracketed vector required.");
            ImmutableArray<string> fields = SplitArguments(value[1..^1]);
            Count(fields, 3);
            return new(Number(fields[0]), Number(fields[1]), Number(fields[2]));
        }
        void Bone(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || inventory is not null && !inventory.Contains(name))
                throw new FormatException($"Bone '{name}' is absent from the supplied inventory.");
        }
    }

    public static NativeMpClothDocument ReadMpCloth(string text, IEnumerable<string>? availableResources = null)
    {
        NativeClothSyntax syntax = Parse(text);
        var bindings = ImmutableArray.CreateBuilder<NativeClothBinding>();
        var diagnostics = ImmutableArray.CreateBuilder<NativeClothDiagnostic>();
        HashSet<string>? resources = availableResources?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (NativeClothCommand call in syntax.Commands)
        {
            if (call.Name != "MeshPartCloth") { diagnostics.Add(new("native_statement_unsupported", $"'{call.Name}' is preserved but not interpreted.")); continue; }
            try
            {
                Count(call.Arguments, 3);
                string resource = Quoted(call.Arguments[0]);
                if (!resource.EndsWith(".phx", StringComparison.OrdinalIgnoreCase) || resource.Contains(':') || resource.StartsWith('/') ||
                    resource.Replace('\\', '/').Split('/').Any(p => p is "." or "..")) throw new FormatException("Portable PHX resource name required.");
                int enabled = int.Parse(call.Arguments[1], CultureInfo.InvariantCulture);
                int flag = int.Parse(call.Arguments[2], CultureInfo.InvariantCulture);
                if (enabled is not (0 or 1) || flag is not (0 or 1)) throw new FormatException("Binding flags must be zero or one.");
                if (resources is not null && !resources.Contains(resource)) throw new FormatException($"PHX resource '{resource}' is missing.");
                bindings.Add(new(resource, enabled, flag));
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            { diagnostics.Add(new("native_binding_invalid", exception.Message, true)); }
        }
        if (bindings.Count == 0) diagnostics.Add(new("native_bindings_empty", "No supported cloth bindings found.", true));
        return new(syntax, bindings.ToImmutable(), diagnostics.ToImmutable());
    }

    public static string WriteMpCloth(IEnumerable<NativeClothBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        string text = string.Join("\n", bindings.Select(b => $"MeshPartCloth({Quote(b.ResourceName)}, {b.Enabled}, {b.Flag})")) + "\n";
        if (!ReadMpCloth(text).IsValid) throw new ArgumentException("Invalid native cloth bindings.", nameof(bindings));
        return text;
    }

    /// <summary>Authors a reviewed subset. Additional calls are parsed and preserved without reinterpretation.</summary>
    public static string WritePhx(int width, int height, IEnumerable<NativeClothNode> nodes, IEnumerable<string>? additionalCalls = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var text = new StringBuilder("!include(\"MeshPartCloth.def\")\nMeshPartCloth()\n{\n");
        text.Append(CultureInfo.InvariantCulture, $"    BonesGridSize({width}, {height})\n");
        foreach (NativeClothNode n in nodes)
            text.Append(CultureInfo.InvariantCulture, $"    Bone({n.X}, {n.Y}, {Quote(n.BoneName)}, {n.Type}, {n.RightDistance:R}, {n.DownDistance:R})\n");
        foreach (string call in additionalCalls ?? [])
        {
            NativeClothSyntax syntax = Parse(call);
            if (syntax.Commands.Length != 1 || syntax.Commands[0].Start != 0 || syntax.Commands[0].Length != call.Length)
                throw new ArgumentException("Additional statements must be single complete calls.", nameof(additionalCalls));
            text.Append("    ").Append(call).Append('\n');
        }
        text.Append("}\n");
        string result = text.ToString();
        NativePhxDocument check = ReadPhx(result);
        if (!check.IsValid) throw new ArgumentException(string.Join("; ", check.Diagnostics.Where(d => d.IsError).Select(d => d.Message)));
        return result;
    }

    public static NativeClothSyntax Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 4 * 1024 * 1024 || text.Contains('\0')) throw new FormatException("Native cloth text is too large or contains NUL.");
        var calls = ImmutableArray.CreateBuilder<NativeClothCommand>();
        int i = 0, braces = 0;
        while (i < text.Length)
        {
            if (SkipTrivia(text, ref i)) continue;
            char c = text[i];
            if (c == '{') { braces++; i++; continue; }
            if (c == '}') { if (--braces < 0) throw new FormatException("Unbalanced cloth braces."); i++; continue; }
            if (c == ';') { i++; continue; }
            int start = i;
            if (c is '!' or '$') i++;
            int identifier = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            if (i == identifier) throw new FormatException($"Expected a cloth statement at character {start}.");
            string name = text[start..i];
            while (SkipTrivia(text, ref i)) { }
            if (i >= text.Length || text[i] != '(') throw new FormatException($"Expected arguments for '{name}'.");
            int argumentsStart = ++i, depth = 1;
            while (i < text.Length && depth > 0)
            {
                if (SkipComment(text, ref i)) continue;
                if (text[i] == '"') { SkipString(text, ref i); continue; }
                if (text[i] == '(') depth++;
                if (text[i] == ')') depth--;
                i++;
            }
            if (depth != 0) throw new FormatException($"Unterminated cloth call '{name}'.");
            calls.Add(new(name, SplitArguments(text[argumentsStart..(i - 1)]), start, i - start));
            if (calls.Count > 65536) throw new FormatException("Too many native cloth statements.");
        }
        if (braces != 0) throw new FormatException("Unbalanced cloth braces.");
        return new(text, calls.ToImmutable());
    }

    private static ImmutableArray<string> SplitArguments(string source)
    {
        var fields = ImmutableArray.CreateBuilder<string>();
        var field = new StringBuilder();
        int i = 0, depth = 0;
        while (i < source.Length)
        {
            if (SkipComment(source, ref i)) { field.Append(' '); continue; }
            char c = source[i];
            if (c == '"') { int start = i; SkipString(source, ref i); field.Append(source[start..i]); continue; }
            if (c is '[' or '(') depth++;
            if (c is ']' or ')') { if (--depth < 0) throw new FormatException("Unbalanced native argument."); }
            if (c == ',' && depth == 0) { fields.Add(field.ToString().Trim()); field.Clear(); }
            else field.Append(c);
            i++;
        }
        if (depth != 0) throw new FormatException("Unbalanced native argument.");
        if (fields.Count > 0 || field.ToString().Trim().Length > 0) fields.Add(field.ToString().Trim());
        if (fields.Any(string.IsNullOrEmpty)) throw new FormatException("Empty native argument.");
        return fields.ToImmutable();
    }

    private static bool SkipTrivia(string text, ref int i)
    {
        if (i < text.Length && char.IsWhiteSpace(text[i])) { i++; return true; }
        return SkipComment(text, ref i);
    }

    private static bool SkipComment(string text, ref int i)
    {
        if (i + 1 >= text.Length || text[i] != '/') return false;
        if (text[i + 1] == '/') { i += 2; while (i < text.Length && text[i] != '\n') i++; return true; }
        if (text[i + 1] != '*') return false;
        int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        if (end < 0) throw new FormatException("Unterminated cloth comment.");
        i = end + 2;
        return true;
    }

    private static void SkipString(string text, ref int i)
    {
        i++;
        while (i < text.Length)
        {
            if (text[i] == '\\') { i += 2; continue; }
            if (text[i++] == '"') return;
        }
        throw new FormatException("Unterminated cloth string.");
    }

    private static string Quoted(string source)
    {
        if (source.Length < 2 || source[0] != '"' || source[^1] != '"') throw new FormatException("Quoted native string required.");
        return source[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
    }
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static void Count(ImmutableArray<string> arguments, int count)
    {
        if (arguments.Length != count) throw new FormatException($"Expected {count} arguments, received {arguments.Length}.");
    }
}

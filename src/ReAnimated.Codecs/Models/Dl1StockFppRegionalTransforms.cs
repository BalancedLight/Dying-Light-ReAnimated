using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Models;

/// <summary>Shape-preserving similarities for a source-sized humanoid and a native FPP reference.</summary>
internal static class Dl1StockFppRegionalTransforms
{
    private static readonly string[] TorsoAnchors = ["spine2", "spine3", "spine1", "spine", "pelvis"];
    internal static Dictionary<string, TransformMatrix> Solve(
        IReadOnlyDictionary<string, TransformMatrix> source,
        IReadOnlyDictionary<string, TransformMatrix> reference,
        IReadOnlySet<string> available,
        Dl1StockFppFitOptions options,
        ImmutableArray<string>.Builder notes)
    {
        var transforms = reference.ToDictionary(p => p.Key,
            p => source.TryGetValue(p.Key, out TransformMatrix input)
                ? Rigid(p.Value) * Rigid(input).InvertedAffine() : TransformMatrix.Identity,
            StringComparer.OrdinalIgnoreCase);
        string anchor = TorsoAnchors
            .FirstOrDefault(name => available.Contains(name) && reference.ContainsKey(name))
            ?? throw new InvalidOperationException("Map a torso or shoulder anchor before fitting the stock FPP rig.");
        TransformMatrix torso = TransformMatrix.CreateTranslation(reference[anchor].Translation - source[anchor].Translation);
        foreach (string name in new[] { "pelvis", "hspine", "spine", "spine1", "spine2", "spine3", "hspine1", "neck", "neck1", "head", "l_clavicle", "r_clavicle" })
            if (transforms.ContainsKey(name)) transforms[name] = torso;

        foreach (string side in new[] { "l", "r" })
        {
            string hand = side + "_hand";
            if (!available.Contains(hand)) continue;
            Require(source, available, side + "_upperarm", side + "_forearm", hand);
            double fromLength = Distance(source, side + "_upperarm", side + "_forearm") + Distance(source, side + "_forearm", hand);
            double toLength = Distance(reference, side + "_upperarm", side + "_forearm") + Distance(reference, side + "_forearm", hand);
            double armScale = Ratio(toLength, fromLength, "arm");
            string clavicle = side + "_clavicle";
            if (!available.Contains("pelvis") && available.Contains(clavicle))
                transforms[clavicle] = Similarity(Rigid(source[clavicle]), Rigid(reference[clavicle]),
                    Ratio(Distance(reference, clavicle, side + "_upperarm"), Distance(source, clavicle, side + "_upperarm"), "shoulder"));
            foreach (string tail in new[] { "upperarm", "forearm", "foretwist", "foretwist1", "foretwistt", "uparmtwist" })
            {
                string name = side + "_" + tail;
                if (reference.TryGetValue(name, out TransformMatrix target) && source.TryGetValue(name, out TransformMatrix input))
                    transforms[name] = Similarity(Rigid(input), Rigid(target), armScale);
            }
            if (!Enumerable.Range(0, 5).All(k => available.Contains(side + $"_finger{k}1") && available.Contains(side + $"_finger{k}3")))
            {
                notes.Add($"The {side} hand has incomplete finger guides; its mapped nodes use rigid reference placement.");
                continue;
            }
            TransformMatrix sourcePalm = Palm(source, side), targetPalm = Palm(reference, side);
            double width = Distance(source, side + "_finger11", side + "_finger41");
            double targetWidth = Distance(reference, side + "_finger11", side + "_finger41");
            double palmScale = side == "l" ? options.LeftPalmScaleOverride ?? Ratio(targetWidth, width, "palm")
                : options.RightPalmScaleOverride ?? Ratio(targetWidth, width, "palm");
            transforms[hand] = Similarity(sourcePalm, targetPalm, palmScale);
            if (transforms.ContainsKey(side + "_hand1")) transforms[side + "_hand1"] = transforms[hand];
            for (int digit = 0; digit < 5; digit++)
            {
                string first = side + $"_finger{digit}1", last = side + $"_finger{digit}3";
                TransformMatrix from = DirectionFrame(source[first].Translation, source[last].Translation,
                    new(sourcePalm.M12, sourcePalm.M22, sourcePalm.M32));
                TransformMatrix to = DirectionFrame(reference[first].Translation, reference[last].Translation,
                    new(targetPalm.M12, targetPalm.M22, targetPalm.M32));
                TransformMatrix transform = Similarity(from, to, Ratio(Distance(reference, first, last), Distance(source, first, last), "finger"));
                for (int joint = 1; joint <= 3; joint++) transforms[side + $"_finger{digit}{joint}"] = transform;
                if (digit == 0 && transforms.ContainsKey(side + "_finger01extra")) transforms[side + "_finger01extra"] = transform;
            }
            notes.Add($"{(side == "l" ? "Left" : "Right")} arm scale {armScale:G4}; palm width scale {palmScale:G4}. Fingers retain one uniform scale per digit.");
        }
        return transforms;
    }

    internal static TransformMatrix Rigid(TransformMatrix matrix)
    {
        TransformMatrix rotation = Dl1CustomModelRigPreparer.OrthonormalizeRotation(matrix);
        return rotation with { M14 = matrix.M14, M24 = matrix.M24, M34 = matrix.M34, M41 = 0, M42 = 0, M43 = 0, M44 = 1 };
    }

    private static TransformMatrix Palm(IReadOnlyDictionary<string, TransformMatrix> globals, string side)
    {
        Vector3D wrist = globals[side + "_hand"].Translation;
        Vector3D forward = (globals[side + "_finger11"].Translation + globals[side + "_finger21"].Translation + globals[side + "_finger31"].Translation) / 3;
        return DirectionFrame(wrist, forward, globals[side + "_finger11"].Translation - globals[side + "_finger41"].Translation);
    }

    private static TransformMatrix DirectionFrame(Vector3D origin, Vector3D end, Vector3D up)
    {
        if (!(end - origin).TryNormalize(out Vector3D x, 1e-10)) throw new InvalidOperationException("A hand or finger has no usable joint span.");
        up -= x * Vector3D.Dot(x, up);
        if (!up.TryNormalize(out Vector3D y, 1e-10)) throw new InvalidOperationException("The hand guides do not define a palm plane.");
        Vector3D z = Vector3D.Cross(x, y).Normalized();
        return new(x.X, y.X, z.X, origin.X, x.Y, y.Y, z.Y, origin.Y, x.Z, y.Z, z.Z, origin.Z, 0, 0, 0, 1);
    }

    private static TransformMatrix Similarity(TransformMatrix source, TransformMatrix target, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new InvalidOperationException("Regional scale must be positive and finite.");
        TransformMatrix rotation = (target with { M14 = 0, M24 = 0, M34 = 0 }) *
            (source with { M14 = 0, M24 = 0, M34 = 0 }).InvertedAffine();
        TransformMatrix result = rotation * TransformMatrix.CreateScale(new(scale, scale, scale));
        Vector3D position = target.Translation - result.TransformDirection(source.Translation);
        return result with { M14 = position.X, M24 = position.Y, M34 = position.Z };
    }

    private static double Distance(IReadOnlyDictionary<string, TransformMatrix> values, string a, string b) =>
        (values[a].Translation - values[b].Translation).Length;

    private static double Ratio(double numerator, double denominator, string region)
    {
        if (!double.IsFinite(numerator) || !double.IsFinite(denominator) || numerator <= 1e-10 || denominator <= 1e-10)
            throw new InvalidOperationException($"The mapped {region} has no usable length.");
        return numerator / denominator;
    }

    private static void Require(IReadOnlyDictionary<string, TransformMatrix> source, IReadOnlySet<string> available, params string[] names)
    {
        foreach (string name in names)
            if (!available.Contains(name) || !source.ContainsKey(name)) throw new InvalidOperationException($"Map '{name}' before preparing stock first-person animations.");
    }
}

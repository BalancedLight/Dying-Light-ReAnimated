using System.Globalization;
using System.Text;

namespace ReAnimated.Retargeting.Mapping;

public sealed record HumanoidBoneSemanticMatch(
    string Role,
    string Evidence,
    double Confidence);

/// <summary>
/// Conservative, name-only humanoid role classifier used to propose mappings
/// between common FBX skeletons and decoded DL1 rigs. Results are suggestions,
/// never deterministic identities: every row produced from this classifier
/// remains subject to explicit mapping review.
/// </summary>
public static class HumanoidBoneSemanticClassifier
{
    private const double NameAliasConfidence = 0.82;

    private static readonly HashSet<string> CanonicalRoles =
        new(StringComparer.Ordinal)
        {
            "body.root",
            "body.pelvis",
            "body.spine.base",
            "body.spine.0",
            "body.spine.1",
            "body.spine.2",
            "body.spine.3",
            "body.neck.0",
            "body.neck.1",
            "body.head",
            "arm.left.clavicle",
            "arm.left.upper",
            "arm.left.lower",
            "hand.left",
            "arm.right.clavicle",
            "arm.right.upper",
            "arm.right.lower",
            "hand.right",
            "leg.left.upper",
            "leg.left.lower",
            "foot.left",
            "toe.left",
            "leg.right.upper",
            "leg.right.lower",
            "foot.right",
            "toe.right",
        };

    public static HumanoidBoneSemanticMatch? Classify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        string canonicalCandidate = normalized.ToLowerInvariant();
        if (CanonicalRoles.Contains(canonicalCandidate))
        {
            return Match(canonicalCandidate, "declared canonical role", 1.0);
        }

        string localName = GetLocalName(normalized);
        string compact = Compact(localName);
        bool isMixamo = normalized.Contains(
            "mixamorig",
            StringComparison.OrdinalIgnoreCase);
        bool isCharacterCreator = normalized.Contains(
            "cc_base",
            StringComparison.OrdinalIgnoreCase) ||
            compact.StartsWith("ccbase", StringComparison.Ordinal);
        bool isUnrealStyle =
            !isCharacterCreator &&
            localName.Contains("spine_", StringComparison.OrdinalIgnoreCase);

        if (compact is "root" or "armature" or "rootbone" or
            "rlboneroot" or "bip01")
        {
            return Match("body.root", localName);
        }

        compact = StripKnownRigPrefix(compact);
        if (compact is "hips" or "hip" or "pelvis")
        {
            return Match("body.pelvis", localName);
        }

        HumanoidBoneSemanticMatch? axial = ClassifyAxial(
            compact,
            localName,
            isMixamo,
            isCharacterCreator,
            isUnrealStyle);
        if (axial is not null)
        {
            return axial;
        }

        if (ContainsExcludedModifier(compact))
        {
            return null;
        }

        (string? side, string sidedBase) = ExtractSide(compact);
        if (side is null || sidedBase.Length == 0)
        {
            return null;
        }

        string? sidedRole = sidedBase switch
        {
            "clavicle" or "shoulder" or "collarbone" =>
                $"arm.{side}.clavicle",
            "upperarm" or "uparm" or "arm" =>
                $"arm.{side}.upper",
            "forearm" or "lowerarm" or "lowarm" =>
                $"arm.{side}.lower",
            "hand" or "wrist" =>
                $"hand.{side}",
            "thigh" or "upleg" or "upperleg" =>
                $"leg.{side}.upper",
            "calf" or "lowerleg" or "lowleg" or "shin" =>
                $"leg.{side}.lower",
            "leg" when isMixamo =>
                $"leg.{side}.lower",
            "foot" or "ankle" =>
                $"foot.{side}",
            "toe" or "toebase" or "ball" =>
                $"toe.{side}",
            _ => ClassifyFinger(sidedBase, side),
        };
        return sidedRole is null
            ? null
            : Match(sidedRole, localName);
    }

    private static HumanoidBoneSemanticMatch? ClassifyAxial(
        string compact,
        string evidence,
        bool isMixamo,
        bool isCharacterCreator,
        bool isUnrealStyle)
    {
        if (compact is "hspine" or "waist")
        {
            return Match(
                isCharacterCreator
                    ? "body.spine.0"
                    : "body.spine.base",
                evidence);
        }

        if (compact == "spine")
        {
            return Match("body.spine.0", evidence);
        }

        if (TryParseNumberedStem(compact, "spine", out int spineIndex))
        {
            int canonicalIndex = spineIndex;
            if (isUnrealStyle)
            {
                canonicalIndex--;
            }
            else if (isCharacterCreator)
            {
                // CC_Base_Waist owns spine.0; Spine01 and Spine02 are the
                // next two torso segments.
                canonicalIndex = spineIndex;
            }
            else if (isMixamo)
            {
                // Mixamo's Spine, Spine1 and Spine2 directly line up with
                // DL1's spine.0, spine.1 and spine.2 roles.
                canonicalIndex = spineIndex;
            }

            if (canonicalIndex is >= 0 and <= 3)
            {
                return Match($"body.spine.{canonicalIndex}", evidence);
            }
        }

        if (compact is "chest" or "upperchest")
        {
            return Match("body.spine.2", evidence);
        }

        if (compact == "neck")
        {
            return Match("body.neck.0", evidence);
        }

        if (TryParseNumberedStem(compact, "neck", out int neckIndex) &&
            neckIndex is >= 0 and <= 1)
        {
            return Match($"body.neck.{neckIndex}", evidence);
        }

        if (isCharacterCreator &&
            TryParseNumberedStem(
                compact,
                "necktwist",
                out int neckTwistIndex) &&
            neckTwistIndex is 1 or 2)
        {
            return Match($"body.neck.{neckTwistIndex - 1}", evidence);
        }

        return compact == "head"
            ? Match("body.head", evidence)
            : null;
    }

    private static string? ClassifyFinger(
        string value,
        string side)
    {
        ReadOnlySpan<(string Name, string Role)> aliases =
        [
            ("thumb", "thumb"),
            ("index", "index"),
            ("mid", "middle"),
            ("middle", "middle"),
            ("ring", "ring"),
            ("pinky", "little"),
            ("little", "little"),
        ];
        foreach ((string alias, string role) in aliases)
        {
            int position = value.IndexOf(alias, StringComparison.Ordinal);
            if (position < 0)
            {
                continue;
            }

            ReadOnlySpan<char> suffix = value.AsSpan(position + alias.Length);
            if (suffix.Length == 0 ||
                !int.TryParse(
                    suffix,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int segment) ||
                segment is < 1 or > 4)
            {
                return null;
            }

            return $"finger.{side}.{role}.{segment}";
        }

        if (!value.StartsWith("finger", StringComparison.Ordinal) ||
            value.Length != 8 ||
            !int.TryParse(
                value.AsSpan(6, 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int family) ||
            !int.TryParse(
                value.AsSpan(7, 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int dl1Segment) ||
            family is < 0 or > 4 ||
            dl1Segment is < 1 or > 4)
        {
            return null;
        }

        string familyRole = family switch
        {
            0 => "thumb",
            1 => "index",
            2 => "middle",
            3 => "ring",
            _ => "little",
        };
        return $"finger.{side}.{familyRole}.{dl1Segment}";
    }

    private static (string? Side, string Base) ExtractSide(string value)
    {
        if (value.StartsWith("left", StringComparison.Ordinal))
        {
            return ("left", value[4..]);
        }

        if (value.StartsWith("right", StringComparison.Ordinal))
        {
            return ("right", value[5..]);
        }

        if (value.EndsWith("left", StringComparison.Ordinal))
        {
            return ("left", value[..^4]);
        }

        if (value.EndsWith("right", StringComparison.Ordinal))
        {
            return ("right", value[..^5]);
        }

        if (value.Length > 1 &&
            value[0] is 'l' or 'r')
        {
            return (
                value[0] == 'l' ? "left" : "right",
                value[1..]);
        }

        if (value.Length > 1 &&
            value[^1] is 'l' or 'r')
        {
            return (
                value[^1] == 'l' ? "left" : "right",
                value[..^1]);
        }

        return (null, value);
    }

    private static bool ContainsExcludedModifier(string value) =>
        value.Contains("twist", StringComparison.Ordinal) ||
        value.Contains("roll", StringComparison.Ordinal) ||
        value.Contains("helper", StringComparison.Ordinal) ||
        value.Contains("socket", StringComparison.Ordinal) ||
        value.Contains("pole", StringComparison.Ordinal) ||
        value.Contains("nub", StringComparison.Ordinal) ||
        value.Contains("end", StringComparison.Ordinal) ||
        value.StartsWith("ik", StringComparison.Ordinal) ||
        value.StartsWith("mch", StringComparison.Ordinal);

    private static bool TryParseNumberedStem(
        string value,
        string stem,
        out int number)
    {
        number = 0;
        return value.StartsWith(stem, StringComparison.Ordinal) &&
               value.Length > stem.Length &&
               int.TryParse(
                   value.AsSpan(stem.Length),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out number);
    }

    private static string StripKnownRigPrefix(string value)
    {
        foreach (string prefix in
                 new[] { "bip01", "ccbase", "rlbone", "def" })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal) &&
                value.Length > prefix.Length)
            {
                return value[prefix.Length..];
            }
        }

        return value;
    }

    private static string GetLocalName(string value)
    {
        int separator = Math.Max(
            value.LastIndexOf(':'),
            value.LastIndexOf('|'));
        return separator >= 0 && separator < value.Length - 1
            ? value[(separator + 1)..]
            : value;
    }

    private static string Compact(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static HumanoidBoneSemanticMatch Match(
        string role,
        string evidence,
        double confidence = NameAliasConfidence) =>
        new(role, evidence, confidence);
}

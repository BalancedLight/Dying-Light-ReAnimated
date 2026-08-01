using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReAnimated.Tests;

public sealed class PythonSuiteAuditTests
{
    private const string RulesFormat =
        "dl-reanimated-python-suite-audit-rules-v1";
    private const string ManifestFormat =
        "dl-reanimated-python-suite-audit-manifest-v1";
    private const int ReviewedNodeCount = 616;
    private const int ReviewedMappedCount = 92;
    private const int ReviewedExclusionCount = 317;
    private const int ReviewedPendingCount = 207;
    private const int MaximumRulesBytes = 512 * 1024;
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RegexTimeout =
        TimeSpan.FromSeconds(1);

    [Fact]
    [Trait("Category", "PythonSuiteAudit")]
    public void ReviewedManifestClassifiesEveryExactPythonRegression()
    {
        string repository = FindRepositoryRoot();
        string rulesPath = Path.Combine(
            repository,
            "tests",
            "fixtures",
            "dl1_python_suite_audit_rules_v1.json");
        string manifestPath = Path.Combine(
            repository,
            "tests",
            "fixtures",
            "dl1_python_suite_audit_v1.json");
        byte[] rulesBytes = ReadBounded(
            rulesPath,
            MaximumRulesBytes);
        byte[] manifestBytes = ReadBounded(
            manifestPath,
            MaximumManifestBytes);
        using JsonDocument rulesDocument =
            JsonDocument.Parse(rulesBytes);
        using JsonDocument manifestDocument =
            JsonDocument.Parse(manifestBytes);
        JsonElement rulesRoot = rulesDocument.RootElement;
        JsonElement manifestRoot = manifestDocument.RootElement;
        Assert.Equal(
            RulesFormat,
            RequiredString(rulesRoot, "format"));
        Assert.Equal(
            ManifestFormat,
            RequiredString(manifestRoot, "format"));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(rulesBytes)),
            RequiredString(manifestRoot, "rulesSha256"));

        JsonElement[] rules = RequiredProperty(rulesRoot, "rules")
            .EnumerateArray()
            .ToArray();
        Assert.NotEmpty(rules);
        Assert.Equal(
            rules.Length,
            rules.Select(rule => RequiredString(rule, "id"))
                .Distinct(StringComparer.Ordinal)
                .Count());
        foreach (JsonElement rule in rules)
        {
            string classification =
                RequiredString(rule, "classification");
            Assert.Contains(
                classification,
                ValidClassifications);
            JsonElement[] evidence = RequiredProperty(
                    rule,
                    "csharpEvidence")
                .EnumerateArray()
                .ToArray();
            if (classification == "applicable_mapped")
            {
                Assert.NotEmpty(evidence);
                Assert.All(
                    evidence,
                    static value =>
                        Assert.False(string.IsNullOrWhiteSpace(
                            value.GetString())));
            }
        }

        JsonElement[] entries = RequiredProperty(
                manifestRoot,
                "entries")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(ReviewedNodeCount, entries.Length);
        Assert.Equal(
            entries.Length,
            entries.Select(entry =>
                    RequiredString(entry, "nodeId"))
                .Distinct(StringComparer.Ordinal)
                .Count());
        var classificationCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        string[] nodeIds = new string[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            JsonElement entry = entries[index];
            Assert.Equal(index, RequiredInt32(entry, "index"));
            string nodeId = RequiredString(entry, "nodeId");
            Assert.StartsWith(
                "tests/test_",
                nodeId,
                StringComparison.Ordinal);
            Assert.Contains("::", nodeId, StringComparison.Ordinal);
            nodeIds[index] = nodeId;
            string classification = RequiredString(
                entry,
                "classification");
            Assert.Contains(
                classification,
                ValidClassifications);
            classificationCounts.TryGetValue(
                classification,
                out int count);
            classificationCounts[classification] = count + 1;

            JsonElement? matchedRule = null;
            foreach (JsonElement rule in rules)
            {
                Match match = Regex.Match(
                    nodeId,
                    RequiredString(rule, "pattern"),
                    RegexOptions.CultureInvariant,
                    RegexTimeout);
                if (match.Success &&
                    match.Index == 0 &&
                    match.Length == nodeId.Length)
                {
                    matchedRule = rule;
                    break;
                }
            }

            Assert.True(
                matchedRule.HasValue,
                $"No reviewed audit rule matched '{nodeId}'.");
            JsonElement actualRule = matchedRule.Value;
            Assert.Equal(
                RequiredString(actualRule, "id"),
                RequiredString(entry, "ruleId"));
            Assert.Equal(
                RequiredString(actualRule, "classification"),
                classification);
            Assert.Equal(
                RequiredString(actualRule, "area"),
                RequiredString(entry, "area"));
        }

        Assert.Equal(
            ReviewedMappedCount,
            classificationCounts["applicable_mapped"]);
        Assert.Equal(
            ReviewedExclusionCount,
            classificationCounts["explicit_exclusion"]);
        Assert.Equal(
            ReviewedPendingCount,
            classificationCounts["still_pending"]);
        Assert.Equal(
            ReviewedNodeCount,
            classificationCounts.Values.Sum());

        JsonElement summary = RequiredProperty(
            manifestRoot,
            "summary");
        Assert.Equal(
            ReviewedNodeCount,
            RequiredInt32(summary, "total"));
        JsonElement byClassification = RequiredProperty(
            summary,
            "byClassification");
        Assert.Equal(
            ReviewedMappedCount,
            RequiredInt32(
                byClassification,
                "applicable_mapped"));
        Assert.Equal(
            ReviewedExclusionCount,
            RequiredInt32(
                byClassification,
                "explicit_exclusion"));
        Assert.Equal(
            ReviewedPendingCount,
            RequiredInt32(
                byClassification,
                "still_pending"));

        byte[] collectionBytes = Encoding.UTF8.GetBytes(
            string.Join('\n', nodeIds) + "\n");
        Assert.Equal(
            Convert.ToHexString(
                SHA256.HashData(collectionBytes)),
            RequiredString(manifestRoot, "collectionSha256"));
    }

    private static byte[] ReadBounded(
        string path,
        int maximumBytes)
    {
        var info = new FileInfo(path);
        Assert.True(info.Exists, $"Required audit file is missing: {path}");
        Assert.InRange(info.Length, 1, maximumBytes);
        return File.ReadAllBytes(path);
    }

    private static readonly string[] ValidClassifications =
    [
        "applicable_mapped",
        "explicit_exclusion",
        "still_pending",
    ];

    private static JsonElement RequiredProperty(
        JsonElement element,
        string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new InvalidDataException(
                $"Python suite audit JSON is missing '{name}'.");

    private static string RequiredString(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetString()
        ?? throw new InvalidDataException(
            $"Python suite audit '{name}' is null.");

    private static int RequiredInt32(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetInt32();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "DLReAnimated.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the DL ReAnimated repository root.");
    }
}

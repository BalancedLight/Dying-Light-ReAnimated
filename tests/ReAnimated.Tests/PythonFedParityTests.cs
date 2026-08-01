using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.Fed;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class PythonFedParityTests
{
    private const string OracleFormat =
        "dl-reanimated-python-csharp-fed-parity-oracle-v1";
    private const int MaximumOracleBytes = 512 * 1024;

    [Fact]
    [Trait("Category", "PythonParity")]
    public void AcceptedInputsPreserveOrderedValuesAndExpressionBehavior()
    {
        using JsonDocument oracle = OpenOracle();
        foreach (JsonElement accepted in RequiredProperty(
                     oracle.RootElement,
                     "acceptedInputs").EnumerateArray())
        {
            string caseId = RequiredString(accepted, "id");
            byte[] payload = ReadPayload(accepted);
            using var stream = new MemoryStream(payload);
            FedDocument document = FedReader.Read(
                stream,
                caseId,
                ReadLimits(accepted));

            AssertNormalizedDocument(
                RequiredProperty(accepted, "normalized"),
                document);

            JsonElement lookup = RequiredProperty(accepted, "lookup");
            FedExpression found = Assert.IsType<FedExpression>(
                document.FindExpression(
                    RequiredString(lookup, "query")));
            int expectedIndex = RequiredInt32(
                lookup,
                "expressionIndex");
            Assert.Same(document.Expressions[expectedIndex], found);
            Assert.Equal(RequiredString(lookup, "name"), found.Name);

            if (accepted.TryGetProperty(
                    "layerNormalization",
                    out JsonElement layer))
            {
                AssertLayerNormalization(document, layer);
            }
        }
    }

    [Fact]
    [Trait("Category", "PythonParity")]
    public void RejectedInputDecisionsMatchIndependentPythonOracle()
    {
        using JsonDocument oracle = OpenOracle();
        foreach (JsonElement rejected in RequiredProperty(
                     oracle.RootElement,
                     "rejectedInputs").EnumerateArray())
        {
            string caseId = RequiredString(rejected, "id");
            byte[] payload = ReadPayload(rejected);
            using var stream = new MemoryStream(payload);
            Exception? exception = Record.Exception(
                () => FedReader.Read(
                    stream,
                    caseId,
                    ReadLimits(rejected)));

            Assert.NotNull(exception);
            Assert.Equal(
                "FedOracleError",
                RequiredString(rejected, "pythonExceptionType"));
            Assert.False(string.IsNullOrWhiteSpace(
                RequiredString(rejected, "pythonMessage")));
            Assert.Contains(
                RequiredString(rejected, "diagnosticFragment"),
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertNormalizedDocument(
        JsonElement expected,
        FedDocument actual)
    {
        JsonElement[] expectedExpressions = RequiredProperty(
                expected,
                "expressions")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            expectedExpressions.Length,
            actual.Expressions.Count);
        for (var expressionIndex = 0;
             expressionIndex < expectedExpressions.Length;
             expressionIndex++)
        {
            JsonElement expectedExpression =
                expectedExpressions[expressionIndex];
            FedExpression actualExpression =
                actual.Expressions[expressionIndex];
            Assert.Equal(
                RequiredString(expectedExpression, "name"),
                actualExpression.Name);
            JsonElement[] expectedWeights = RequiredProperty(
                    expectedExpression,
                    "weights")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(
                expectedWeights.Length,
                actualExpression.Weights.Count);
            for (var weightIndex = 0;
                 weightIndex < expectedWeights.Length;
                 weightIndex++)
            {
                JsonElement expectedWeight =
                    expectedWeights[weightIndex];
                FedMorphWeight actualWeight =
                    actualExpression.Weights[weightIndex];
                Assert.Equal(
                    RequiredString(expectedWeight, "morphName"),
                    actualWeight.MorphName);
                uint expectedBits = uint.Parse(
                    RequiredString(expectedWeight, "weightBitsHex"),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture);
                Assert.Equal(
                    expectedBits,
                    unchecked((uint)BitConverter.SingleToInt32Bits(
                        actualWeight.Weight)));
            }
        }

        JsonElement[] expectedDiagnostics = RequiredProperty(
                expected,
                "diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            expectedDiagnostics.Length,
            actual.Diagnostics.Count);
        for (var diagnosticIndex = 0;
             diagnosticIndex < expectedDiagnostics.Length;
             diagnosticIndex++)
        {
            JsonElement expectedDiagnostic =
                expectedDiagnostics[diagnosticIndex];
            FedDiagnostic actualDiagnostic =
                actual.Diagnostics[diagnosticIndex];
            Assert.Equal(
                RequiredString(expectedDiagnostic, "code"),
                actualDiagnostic.Code);
            Assert.Equal(
                Enum.Parse<FedDiagnosticSeverity>(
                    RequiredString(expectedDiagnostic, "severity"),
                    ignoreCase: false),
                actualDiagnostic.Severity);
            Assert.Equal(
                RequiredString(expectedDiagnostic, "message"),
                actualDiagnostic.Message);
            Assert.Equal(
                OptionalInt32(expectedDiagnostic, "expressionIndex"),
                actualDiagnostic.ExpressionIndex);
            Assert.Equal(
                OptionalInt32(expectedDiagnostic, "weightIndex"),
                actualDiagnostic.WeightIndex);
        }
    }

    private static void AssertLayerNormalization(
        FedDocument document,
        JsonElement expected)
    {
        string[] targetMorphs = RequiredProperty(
                expected,
                "targetMorphs")
            .EnumerateArray()
            .Select(static value =>
                value.GetString() ??
                throw new InvalidDataException(
                    "FED target morph name is null."))
            .ToArray();
        var rig = new RigDefinition(
            "python-fed-parity",
            "Python FED parity",
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    TransformTRS.Identity),
            ],
            targetMorphs.Select((name, index) =>
                new MorphChannelDefinition(index, name)));
        Dictionary<string, string> mapping = RequiredProperty(
                expected,
                "mapping")
            .EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property =>
                    property.Value.GetString() ??
                    throw new InvalidDataException(
                        "FED mapping target is null."),
                StringComparer.OrdinalIgnoreCase);

        FedLayerBuildResult result = FedDomainAdapter.CreateLayer(
            document,
            RequiredInt32(expected, "expressionIndex"),
            rig,
            mapping);
        JsonElement[] expectedTracks = RequiredProperty(
                expected,
                "tracks")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(expectedTracks.Length, result.Layer.Tracks.Length);
        for (var index = 0; index < expectedTracks.Length; index++)
        {
            JsonElement expectedTrack = expectedTracks[index];
            MorphEditTrack actualTrack = result.Layer.Tracks[index];
            Assert.Equal(
                RequiredString(expectedTrack, "morphName"),
                actualTrack.MorphName);
            ScalarKeyframe keyframe =
                Assert.Single(actualTrack.Keyframes);
            Assert.Equal(
                RequiredDouble(expectedTrack, "value"),
                keyframe.Value);
        }

        JsonElement compatibility = RequiredProperty(
            expected,
            "compatibility");
        Assert.Equal(
            RequiredInt32(compatibility, "sourceWeightCount"),
            result.Compatibility.SourceWeightCount);
        Assert.Equal(
            RequiredInt32(compatibility, "resolvedWeightCount"),
            result.Compatibility.ResolvedWeightCount);
        Assert.Equal(
            RequiredInt32(compatibility, "resolvedTargetCount"),
            result.Compatibility.ResolvedTargetCount);
        Assert.Equal(
            RequiredBoolean(compatibility, "isComplete"),
            result.Compatibility.IsComplete);
        Assert.Equal(
            RequiredProperty(
                    compatibility,
                    "missingSourceMorphNames")
                .EnumerateArray()
                .Select(static value =>
                    value.GetString() ??
                    throw new InvalidDataException(
                        "FED missing morph name is null.")),
            result.Compatibility.MissingSourceMorphNames);

        JsonElement[] expectedDiagnostics = RequiredProperty(
                expected,
                "diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            expectedDiagnostics.Length,
            result.Diagnostics.Length);
        for (var index = 0;
             index < expectedDiagnostics.Length;
             index++)
        {
            JsonElement expectedDiagnostic =
                expectedDiagnostics[index];
            FedDiagnostic actualDiagnostic =
                result.Diagnostics[index];
            Assert.Equal(
                RequiredString(expectedDiagnostic, "code"),
                actualDiagnostic.Code);
            Assert.Equal(
                Enum.Parse<FedDiagnosticSeverity>(
                    RequiredString(expectedDiagnostic, "severity"),
                    ignoreCase: false),
                actualDiagnostic.Severity);
            Assert.Equal(
                OptionalInt32(expectedDiagnostic, "expressionIndex"),
                actualDiagnostic.ExpressionIndex);
            Assert.Equal(
                OptionalInt32(expectedDiagnostic, "weightIndex"),
                actualDiagnostic.WeightIndex);
        }
    }

    private static byte[] ReadPayload(JsonElement element)
    {
        byte[] payload = Convert.FromBase64String(
            RequiredString(element, "payloadBase64"));
        Assert.Equal(
            RequiredInt32(element, "payloadBytes"),
            payload.Length);
        Assert.Equal(
            RequiredString(element, "payloadSha256"),
            Convert.ToHexString(SHA256.HashData(payload)));
        return payload;
    }

    private static FedLimits ReadLimits(JsonElement element) =>
        new()
        {
            RejectDuplicateNames = RequiredBoolean(
                element,
                "rejectDuplicateNames"),
        };

    private static JsonDocument OpenOracle()
    {
        string repository = FindRepositoryRoot();
        string path = Path.Combine(
            repository,
            "tests",
            "fixtures",
            "dl1_python_csharp_fed_parity_v1.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Checked-in FED compatibility fixture was not found.",
                path);
        }

        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(
            bytes.Length <= MaximumOracleBytes,
            $"FED parity oracle is {bytes.Length} bytes; " +
            $"maximum is {MaximumOracleBytes}.");
        JsonDocument document = JsonDocument.Parse(bytes);
        try
        {
            Assert.Equal(
                OracleFormat,
                RequiredString(document.RootElement, "format"));
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static JsonElement RequiredProperty(
        JsonElement element,
        string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new InvalidDataException(
                $"FED parity oracle is missing '{name}'.");

    private static string RequiredString(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetString()
        ?? throw new InvalidDataException(
            $"FED parity oracle '{name}' is null.");

    private static int RequiredInt32(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetInt32();

    private static int? OptionalInt32(
        JsonElement element,
        string name)
    {
        JsonElement value = RequiredProperty(element, name);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : value.GetInt32();
    }

    private static double RequiredDouble(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetDouble();

    private static bool RequiredBoolean(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetBoolean();

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

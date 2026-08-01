using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.Anm2;

namespace ReAnimated.Tests;

public sealed class AnimationScrEventParityTests
{
    private const string OracleFormat =
        "dl-reanimated-python-csharp-animation-scr-event-parity-v1";
    private const int MaximumOracleBytes = 512 * 1024;

    [Fact]
    [Trait("Category", "PythonParity")]
    public void EventBearingStockLayoutParseAndOpaquePatchMatchPythonOracle()
    {
        using JsonDocument document = OpenOracle();
        JsonElement root = document.RootElement;
        JsonElement scope = RequiredProperty(root, "scope");
        Assert.Equal("Dying Light 1", RequiredString(scope, "game"));
        Assert.Equal("opaque", RequiredString(scope, "eventSemantics"));
        Assert.False(
            RequiredProperty(scope, "eventEncodingSupported").GetBoolean());
        Assert.False(
            RequiredProperty(scope, "retailPayloadEmbedded").GetBoolean());
        Assert.Equal(
            AnimationScrCodec.RecordSize,
            RequiredInt32(root, "recordSize"));
        Assert.Equal(
            AnimationScrCodec.EventRecordSize,
            RequiredInt32(root, "eventRecordSize"));

        JsonElement canonical = RequiredProperty(root, "canonical");
        int eventTableOffset = RequiredInt32(
            canonical,
            "eventTableOffset");
        int eventTableLength = RequiredInt32(
            canonical,
            "eventTableLength");
        byte[] expectedEventTable = Convert.FromBase64String(
            RequiredString(canonical, "eventTableBase64"));
        Assert.Equal(eventTableLength, expectedEventTable.Length);
        Assert.Equal(
            RequiredString(canonical, "eventTableSha256"),
            Sha256(expectedEventTable));

        AnimationScrSections original = ReadSections(
            RequiredProperty(canonical, "original"));
        ParsedAnimationScr originalParsed = AssertSections(
            RequiredProperty(canonical, "original"),
            original);
        Assert.Equal(eventTableOffset, originalParsed.OpaquePayloadOffset);
        Assert.Equal(eventTableLength, originalParsed.OpaquePayloadLength);
        Assert.Equal(eventTableLength, originalParsed.ExpectedEventTableLength);
        Assert.Equal(3UL, originalParsed.TotalDeclaredEventCount);
        Assert.True(originalParsed.HasCanonicalEventTableLayout);
        Assert.Equal(
            [2U, 0U, 1U],
            originalParsed.Sequences
                .Select(static sequence => sequence.RawEventCount)
                .ToArray());
        Assert.Equal(
            expectedEventTable,
            original.RecordsAndNames
                .AsSpan(eventTableOffset, eventTableLength)
                .ToArray());

        JsonElement patchOverride = RequiredProperty(
            canonical,
            "patchOverride");
        AnimationScrSections patched = AnimationScrCodec.PatchRanges(
            original,
            new Dictionary<
                string,
                (float Start, float End, float FramesPerSecond)>
            {
                [RequiredString(patchOverride, "name")] =
                    (
                        (float)RequiredDouble(
                            patchOverride,
                            "startFrame"),
                        (float)RequiredDouble(
                            patchOverride,
                            "endFrame"),
                        (float)RequiredDouble(
                            patchOverride,
                            "framesPerSecond")),
            });
        ParsedAnimationScr patchedParsed = AssertSections(
            RequiredProperty(canonical, "patched"),
            patched);

        Assert.Equal(original.IndexAndNames, patched.IndexAndNames);
        Assert.Equal(
            expectedEventTable,
            patched.RecordsAndNames
                .AsSpan(eventTableOffset, eventTableLength)
                .ToArray());
        Assert.Equal(
            originalParsed.TotalDeclaredEventCount,
            patchedParsed.TotalDeclaredEventCount);
        Assert.True(patchedParsed.HasCanonicalEventTableLayout);
        Assert.Equal(
            RequiredProperty(canonical, "patchChangedOffsets")
                .EnumerateArray()
                .Select(static value => value.GetInt32())
                .ToArray(),
            ChangedOffsets(
                original.RecordsAndNames,
                patched.RecordsAndNames));
    }

    [Fact]
    [Trait("Category", "PythonParity")]
    public void EventBearingStockLayoutRejectionsMatchPythonOracle()
    {
        using JsonDocument document = OpenOracle();
        foreach (JsonElement rejection in
                 RequiredProperty(
                     document.RootElement,
                     "rejectedInputs").EnumerateArray())
        {
            string caseId = RequiredString(rejection, "id");
            string operation = RequiredString(rejection, "operation");
            string pythonExceptionType = RequiredString(
                rejection,
                "pythonExceptionType");
            Assert.True(
                pythonExceptionType is "ValueError" or "NotImplementedError",
                $"Unexpected Python rejection type '{pythonExceptionType}'.");
            Assert.False(
                string.IsNullOrWhiteSpace(
                    RequiredString(rejection, "pythonMessage")));

            AnimationScrSections sections = ReadSections(rejection);
            Exception? exception = Record.Exception(
                () => ExecuteRejection(operation, sections));
            Exception actual = Assert.IsAssignableFrom<Exception>(
                exception);
            AssertExpectedExceptionType(caseId, actual);
            AssertDiagnostic(caseId, actual.Message);
        }
    }

    [Fact]
    [Trait("Gate", "DL1AnimationScrRetail155")]
    public void Retail155MarkerPairParsesCanonicalEventLayout()
    {
        using JsonDocument document = OpenOracle();
        JsonElement canonical = RequiredProperty(
            document.RootElement,
            "canonical");
        AnimationScrSections authored = ReadSections(
            RequiredProperty(canonical, "original"));
        byte[] retailSection0 = authored.RecordsAndNames.ToArray();
        int declaredSequenceCount = RequiredInt32(
            RequiredProperty(
                RequiredProperty(canonical, "original"),
                "parsed"),
            "declaredSequenceCount");
        for (var index = 0; index < declaredSequenceCount; index++)
        {
            int recordOffset = index * AnimationScrCodec.RecordSize;
            BinaryPrimitives.WriteUInt32LittleEndian(
                retailSection0.AsSpan(recordOffset + 4),
                AnimationScrCodec.Retail155RecordMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(
                retailSection0.AsSpan(recordOffset + 52),
                AnimationScrCodec.Retail155RecordSentinel);
        }

        ParsedAnimationScr parsed = AnimationScrCodec.Parse(
            new AnimationScrSections(
                retailSection0,
                authored.IndexAndNames));

        Assert.Equal(declaredSequenceCount, parsed.Sequences.Length);
        Assert.Equal(
            ["idle_event", "run_event", "turn_event"],
            parsed.Sequences.Select(static sequence => sequence.Name));
        Assert.Equal(3UL, parsed.TotalDeclaredEventCount);
        Assert.Equal(
            RequiredInt32(canonical, "eventTableLength"),
            parsed.OpaquePayloadLength);
        Assert.True(parsed.HasCanonicalEventTableLayout);
    }

    private static void ExecuteRejection(
        string operation,
        AnimationScrSections sections)
    {
        switch (operation)
        {
            case "parse":
                _ = AnimationScrCodec.Parse(sections);
                break;
            case "patch":
                _ = AnimationScrCodec.PatchRanges(
                    sections,
                    new Dictionary<
                        string,
                        (float Start, float End, float FramesPerSecond)>
                    {
                        ["not_present"] = (0, 1, 30),
                    });
                break;
            case "append":
                _ = AnimationScrCodec.Append(
                    sections,
                    [
                        new AnimationScrSequence(
                            "new_event_clip",
                            "new_event_clip.anm2",
                            0,
                            1,
                            30),
                    ]);
                break;
            default:
                throw new InvalidDataException(
                    $"Unknown AnimationScr rejection operation '{operation}'.");
        }
    }

    private static void AssertExpectedExceptionType(
        string caseId,
        Exception exception)
    {
        switch (caseId)
        {
            case "append-event-layout":
                Assert.IsType<NotSupportedException>(exception);
                break;
            case "patch-missing-event-sequence":
                Assert.IsType<KeyNotFoundException>(exception);
                break;
            case "event-name-offset-outside":
            case "event-name-unterminated":
            case "event-name-table-missing":
                Assert.IsType<InvalidDataException>(exception);
                break;
            default:
                throw new InvalidDataException(
                    $"Unknown AnimationScr rejection recipe '{caseId}'.");
        }
    }

    private static void AssertDiagnostic(
        string caseId,
        string message)
    {
        string expectedFragment = caseId switch
        {
            "append-event-layout" => "auxiliary/event",
            "patch-missing-event-sequence" => "missing",
            "event-name-offset-outside" => "outside",
            "event-name-unterminated" => "NUL terminated",
            "event-name-table-missing" => "name",
            _ => throw new InvalidDataException(
                $"Unknown AnimationScr rejection recipe '{caseId}'."),
        };
        Assert.Contains(
            expectedFragment,
            message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedAnimationScr AssertSections(
        JsonElement expected,
        AnimationScrSections actual)
    {
        byte[] expectedSection0 = Convert.FromBase64String(
            RequiredString(expected, "section0Base64"));
        byte[] expectedSection1 = Convert.FromBase64String(
            RequiredString(expected, "section1Base64"));
        Assert.Equal(expectedSection0, actual.RecordsAndNames);
        Assert.Equal(expectedSection1, actual.IndexAndNames);
        Assert.Equal(
            RequiredString(expected, "section0Sha256"),
            Sha256(actual.RecordsAndNames));
        Assert.Equal(
            RequiredString(expected, "section1Sha256"),
            Sha256(actual.IndexAndNames));

        ParsedAnimationScr parsed = AnimationScrCodec.Parse(actual);
        JsonElement expectedParsed = RequiredProperty(expected, "parsed");
        Assert.Equal(
            RequiredInt32(expectedParsed, "declaredSequenceCount"),
            parsed.DeclaredSequenceCount);
        Assert.Equal(
            RequiredInt32(expectedParsed, "nameTableOffset"),
            parsed.NameTableOffset);
        JsonElement[] expectedSequences = RequiredProperty(
                expectedParsed,
                "sequences")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(expectedSequences.Length, parsed.Sequences.Length);
        for (var index = 0; index < expectedSequences.Length; index++)
        {
            JsonElement expectedSequence = expectedSequences[index];
            ParsedAnimationScrSequence actualSequence =
                parsed.Sequences[index];
            Assert.Equal(
                RequiredString(expectedSequence, "name"),
                actualSequence.Name);
            Assert.Equal(
                RequiredInt32(expectedSequence, "nameOffset"),
                actualSequence.NameOffset);
            Assert.Equal(
                RequiredInt32(expectedSequence, "recordOffset"),
                actualSequence.RecordOffset);
            Assert.Equal(
                RequiredInt32(expectedSequence, "enabled"),
                actualSequence.Enabled);
            Assert.Equal(
                (float)RequiredDouble(expectedSequence, "blend"),
                actualSequence.Blend);
            Assert.Equal(
                (float)RequiredDouble(
                    expectedSequence,
                    "framesPerSecond"),
                actualSequence.FramesPerSecond);
            Assert.Equal(
                (float)RequiredDouble(expectedSequence, "startFrame"),
                actualSequence.StartFrame);
            Assert.Equal(
                (float)RequiredDouble(expectedSequence, "endFrame"),
                actualSequence.EndFrame);
            int eventCount = RequiredInt32(
                expectedSequence,
                "eventCount");
            Assert.Equal(eventCount, actualSequence.EventCount);
            Assert.Equal(checked((uint)eventCount), actualSequence.RawEventCount);
        }

        return parsed;
    }

    private static AnimationScrSections ReadSections(
        JsonElement element) =>
        new(
            Convert.FromBase64String(
                RequiredString(element, "section0Base64")),
            Convert.FromBase64String(
                RequiredString(element, "section1Base64")));

    private static int[] ChangedOffsets(
        byte[] before,
        byte[] after)
    {
        Assert.Equal(before.Length, after.Length);
        return before
            .Select(
                (value, index) =>
                    value == after[index]
                        ? -1
                        : index)
            .Where(static index => index >= 0)
            .ToArray();
    }

    private static JsonDocument OpenOracle()
    {
        string repository = FindRepositoryRoot();
        string path = Path.Combine(
            repository,
            "tests",
            "fixtures",
            "dl1_animation_scr_event_parity_v1.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Checked-in AnimationScr event-layout compatibility fixture was not found.",
                path);
        }

        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(
            bytes.Length <= MaximumOracleBytes,
            $"AnimationScr event-layout parity oracle is {bytes.Length} " +
            $"bytes; maximum is {MaximumOracleBytes}.");
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

    private static string Sha256(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload));

    private static JsonElement RequiredProperty(
        JsonElement element,
        string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new InvalidDataException(
                $"AnimationScr event parity oracle is missing '{name}'.");

    private static string RequiredString(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetString()
        ?? throw new InvalidDataException(
            $"AnimationScr event parity oracle '{name}' is null.");

    private static int RequiredInt32(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetInt32();

    private static double RequiredDouble(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetDouble();

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

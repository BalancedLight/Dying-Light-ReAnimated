using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.Anm2;

namespace ReAnimated.Tests;

public sealed class PythonOracleParityTests
{
    private const string OracleFormat = "dl-reanimated-python-csharp-parity-oracle-v1";
    private const int MaximumOracleBytes = 4 * 1024 * 1024;

    [Fact]
    [Trait("Category", "PythonParity")]
    public void Dl1Anm2AndNameHashSubsetMatchesVersionedPythonOracle()
    {
        string repository = FindRepositoryRoot();
        string oraclePath = ResolveOraclePath(repository);
        byte[] oracleBytes = File.ReadAllBytes(oraclePath);
        Assert.True(
            oracleBytes.Length <= MaximumOracleBytes,
            $"Parity oracle is {oracleBytes.Length} bytes; maximum is {MaximumOracleBytes}.");

        using JsonDocument document = JsonDocument.Parse(oracleBytes);
        JsonElement root = document.RootElement;
        Assert.Equal(OracleFormat, RequiredString(root, "format"));
        double tolerance = RequiredDouble(
            RequiredProperty(root, "scope"),
            "semanticAbsoluteTolerance");

        CompareNameHashes(root);
        ComparePackedGroups(root);
        CompareGeneratedAnm2(root, tolerance);
        CompareStockAnm2(root, repository, tolerance);
    }

    private static void CompareNameHashes(JsonElement root)
    {
        foreach (JsonElement item in RequiredProperty(root, "nameHashes").EnumerateArray())
        {
            string name = RequiredString(item, "name");
            uint expected = ParseHexUInt32(RequiredString(item, "hashHex"));
            Assert.Equal(expected, Dl1NameHash.Compute(name));
        }

        foreach (JsonElement item in
                 RequiredProperty(root, "rejectedNonAsciiNameHashes").EnumerateArray())
        {
            string name = item.GetString()
                ?? throw new InvalidDataException("Rejected name hash entry is null.");
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => Dl1NameHash.Compute(name));
            Assert.Contains("ASCII", error.Message, StringComparison.Ordinal);
        }
    }

    private static void ComparePackedGroups(JsonElement root)
    {
        foreach (JsonElement item in RequiredProperty(root, "packedGroups").EnumerateArray())
        {
            IReadOnlyList<IReadOnlyList<short>> frames = RequiredProperty(item, "frames")
                .EnumerateArray()
                .Select(frame => (IReadOnlyList<short>)frame
                    .EnumerateArray()
                    .Select(value => value.GetInt16())
                    .ToArray())
                .ToArray();
            byte[] actual = Anm2PackedGroupCodec.Encode(frames);
            byte[] expected = Convert.FromHexString(RequiredString(item, "encodedHex"));
            Assert.Equal(expected, actual);
            Assert.Equal(
                RequiredString(item, "sha256"),
                Convert.ToHexString(SHA256.HashData(actual)));
        }
    }

    private static void CompareGeneratedAnm2(JsonElement root, double tolerance)
    {
        JsonElement oracle = RequiredProperty(root, "generatedAnm2");
        Assert.Equal(
            "two-track-direct-packed-scale-v1",
            RequiredString(oracle, "recipe"));
        int frameCount = RequiredInt32(
            RequiredProperty(oracle, "header"),
            "frameCount");
        ImmutableArray<uint> descriptors = RequiredProperty(oracle, "descriptors")
            .EnumerateArray()
            .Select(value => ParseHexUInt32(
                value.GetString()
                ?? throw new InvalidDataException("ANM2 descriptor is null.")))
            .ToImmutableArray();
        ImmutableArray<Anm2PackedComponents> packed = RequiredProperty(
                oracle,
                "packedComponentMasks")
            .EnumerateArray()
            .Select(value => (Anm2PackedComponents)value.GetUInt16())
            .ToImmutableArray();

        byte[] bytes = Anm2PayloadWriter.Build(
            new Anm2Header(
                Anm2Header.Dl1FormatVersion,
                Anm2Header.Dl1SamplerVersion,
                checked((ushort)frameCount),
                checked((ushort)descriptors.Length),
                1,
                0,
                0,
                1,
                0,
                0),
            descriptors,
            BuildGeneratedFrames(frameCount),
            packed);

        Assert.Equal(RequiredInt32(oracle, "byteLength"), bytes.Length);
        Assert.Equal(
            RequiredString(oracle, "sourceSha256"),
            Convert.ToHexString(SHA256.HashData(bytes)));
        CompareAnm2Payload(oracle, Anm2Reader.Read(bytes), tolerance);
    }

    private static ImmutableArray<Anm2Frame> BuildGeneratedFrames(int frameCount)
    {
        var frames = ImmutableArray.CreateBuilder<Anm2Frame>(frameCount);
        for (var frame = 0; frame < frameCount; frame++)
        {
            frames.Add(new Anm2Frame(
            [
                new Anm2TrackFrame(
                    (frame - 18) * 0.03125f,
                    ((frame % 9) - 4) * 0.0625f,
                    0.125f,
                    frame * 0.015625f,
                    -frame * 0.0078125f,
                    2f,
                    1f,
                    1f,
                    1f),
                new Anm2TrackFrame(
                    0f,
                    0f,
                    0f,
                    2f,
                    -3f,
                    4f,
                    1f + (frame / 128f),
                    1f - (frame / 256f),
                    0.5f + ((frame % 5) / 512f)),
            ]));
        }

        return frames.MoveToImmutable();
    }

    private static void CompareStockAnm2(
        JsonElement root,
        string repository,
        double tolerance)
    {
        string referenceRoot = Path.GetFullPath(
            Path.Combine(
                repository,
                "tests",
                "ReAnimated.Tests",
                "Fixtures",
                "Anm2"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        foreach (JsonElement oracle in RequiredProperty(root, "stockAnm2").EnumerateArray())
        {
            string name = RequiredString(oracle, "name");
            string path = Path.GetFullPath(Path.Combine(referenceRoot, name));
            Assert.StartsWith(referenceRoot, path, StringComparison.OrdinalIgnoreCase);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(RequiredInt32(oracle, "byteLength"), bytes.Length);
            Assert.Equal(
                RequiredString(oracle, "sourceSha256"),
                Convert.ToHexString(SHA256.HashData(bytes)));

            Anm2Clip clip = Anm2Reader.Read(bytes, name);
            Assert.Equal(
                RequiredString(oracle, "preservingRoundTripSha256"),
                Convert.ToHexString(SHA256.HashData(clip.EncodePreservingBody().Span)));
            CompareAnm2Payload(oracle, clip, tolerance);
        }
    }

    private static void CompareAnm2Payload(
        JsonElement oracle,
        Anm2Clip clip,
        double tolerance)
    {
        JsonElement header = RequiredProperty(oracle, "header");
        Assert.Equal(RequiredUInt16(header, "formatVersion"), clip.Header.FormatVersion);
        Assert.Equal(RequiredUInt16(header, "samplerVersion"), clip.Header.SamplerVersion);
        Assert.Equal(RequiredUInt16(header, "frameCount"), clip.Header.FrameCount);
        Assert.Equal(RequiredUInt16(header, "trackCount"), clip.Header.TrackCount);
        Assert.Equal(RequiredUInt16(header, "pageCount"), clip.Header.PageCount);
        Assert.Equal(RequiredUInt16(header, "pageOffset"), clip.Header.PageOffset);
        Assert.Equal(RequiredUInt32(header, "declaredLength"), clip.Header.DeclaredLength);
        Assert.Equal(RequiredUInt32(header, "durationKeyCount"), clip.Header.DurationKeyCount);
        Assert.Equal(RequiredUInt32(header, "unknown24"), clip.Header.Unknown24);
        Assert.Equal(RequiredUInt32(header, "unknown28"), clip.Header.Unknown28);

        uint[] descriptors = RequiredProperty(oracle, "descriptors")
            .EnumerateArray()
            .Select(value => ParseHexUInt32(
                value.GetString()
                ?? throw new InvalidDataException("ANM2 descriptor is null.")))
            .ToArray();
        Assert.Equal(descriptors, clip.TrackDescriptors);
        Assert.Equal(
            RequiredProperty(oracle, "pageFrameSpans")
                .EnumerateArray()
                .Select(value => value.GetUInt16())
                .ToArray(),
            clip.PageFrameSpans);

        int[] trackIndices = RequiredProperty(oracle, "selectedTrackIndices")
            .EnumerateArray()
            .Select(value => value.GetInt32())
            .ToArray();
        foreach (JsonElement sampleOracle in RequiredProperty(oracle, "samples").EnumerateArray())
        {
            Anm2DecodedSample actual = Anm2SemanticDecoder.Sample(
                clip,
                RequiredDouble(sampleOracle, "requestedTime"));
            Assert.Equal(RequiredInt32(sampleOracle, "pageIndex"), actual.PageIndex);
            Assert.Equal(RequiredInt32(sampleOracle, "tableIndex"), actual.TableIndex);
            Assert.Equal(RequiredInt32(sampleOracle, "frameInSlot"), actual.FrameInSlot);
            Assert.InRange(
                Math.Abs(RequiredDouble(sampleOracle, "fraction") - actual.Fraction),
                0,
                tolerance);

            JsonElement.ArrayEnumerator expectedTracks =
                RequiredProperty(sampleOracle, "tracks").EnumerateArray();
            var selected = expectedTracks.ToArray();
            Assert.Equal(trackIndices.Length, selected.Length);
            for (var selectedIndex = 0; selectedIndex < trackIndices.Length; selectedIndex++)
            {
                int trackIndex = trackIndices[selectedIndex];
                Assert.InRange(trackIndex, 0, actual.Frame.Tracks.Length - 1);
                float[] expectedComponents = selected[selectedIndex]
                    .EnumerateArray()
                    .Select(value => value.GetSingle())
                    .ToArray();
                Assert.Equal(9, expectedComponents.Length);
                for (var component = 0; component < expectedComponents.Length; component++)
                {
                    Assert.InRange(
                        Math.Abs(
                            expectedComponents[component] -
                            actual.Frame.Tracks[trackIndex][component]),
                        0,
                        tolerance);
                }
            }
        }
    }

    private static string ResolveOraclePath(string repository)
    {
        string? configured = Environment.GetEnvironmentVariable(
            "DLR_PYTHON_PARITY_ORACLE");
        string path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                repository,
                "tests",
                "fixtures",
                "dl1_python_csharp_parity_v1.json")
            : Path.GetFullPath(configured);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "DL1 Python parity oracle was not found. Run tools/validate_dl1_parity.ps1.",
                path);
        }

        return path;
    }

    private static uint ParseHexUInt32(string value)
    {
        string text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;
        return uint.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    private static JsonElement RequiredProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new InvalidDataException($"Parity oracle is missing '{name}'.");

    private static string RequiredString(JsonElement element, string name) =>
        RequiredProperty(element, name).GetString()
        ?? throw new InvalidDataException($"Parity oracle '{name}' is null.");

    private static double RequiredDouble(JsonElement element, string name) =>
        RequiredProperty(element, name).GetDouble();

    private static int RequiredInt32(JsonElement element, string name) =>
        RequiredProperty(element, name).GetInt32();

    private static ushort RequiredUInt16(JsonElement element, string name) =>
        RequiredProperty(element, name).GetUInt16();

    private static uint RequiredUInt32(JsonElement element, string name) =>
        RequiredProperty(element, name).GetUInt32();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DLReAnimated.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the DL ReAnimated repository root.");
    }
}

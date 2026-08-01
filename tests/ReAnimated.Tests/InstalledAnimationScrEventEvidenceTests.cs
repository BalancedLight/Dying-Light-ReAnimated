using System.Buffers.Binary;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledAnimationScrEventEvidenceTests
{
    private const string RunEnvironmentVariable =
        "DLR_RUN_INSTALLED_ANIMATION_SCR_EVENT_EVIDENCE";
    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";
    private static readonly IReadOnlyDictionary<
        string,
        InstalledAnimationScrControl> ExpectedResources =
        new Dictionary<string, InstalledAnimationScrControl>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["anims_man_all"] = new(6849, 7698, 64092, 769104),
            ["anims_player"] = new(6850, 5925, 5511, 66132),
            ["anims_player_man_all"] =
                new(6851, 12689, 68679, 824148),
        };

    private readonly ITestOutputHelper _output;

    public InstalledAnimationScrEventEvidenceTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 180_000)]
    [Trait("Gate", "DL1InstalledAnimationScrEventEvidence")]
    public async Task Installed155StockScriptsUseCanonicalEventLayout()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    RunEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine(
                $"NOT EXERCISED: set {RunEnvironmentVariable}=1.");
            return;
        }

        Dl1InstallLocation install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static candidate => candidate.IsValid)
            ?? throw new InvalidOperationException(
                "No complete Steam Dying Light 1 installation was discovered.");
        Dl1InstalledBuildFingerprint fingerprint =
            await new Dl1InstalledBuildFingerprintService()
                .ReadAsync(install.InstallPath);
        Assert.Equal(
            ValidatedBuildFingerprint,
            fingerprint.BuildFingerprint,
            ignoreCase: true);

        string archivePath = Path.Combine(
            install.InstallPath,
            "DW",
            "Data",
            "common_anims_PC.rpack");
        Assert.True(
            File.Exists(archivePath),
            $"Installed animation archive was not found at '{archivePath}'.");
        Rp6lArchive archive = await Rp6lArchive.OpenAsync(archivePath);
        string temporaryDirectory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(
                        temporaryDirectory,
                        "cache"),
                    MaximumMemoryBytes = 32 * 1024 * 1024,
                    MaximumMemoryEntryBytes = 16 * 1024 * 1024,
                    MaximumDiskBytes = 512 * 1024 * 1024,
                });
            foreach ((string name, InstalledAnimationScrControl expected) in
                     ExpectedResources)
            {
                await AssertStockScriptAsync(
                    archive,
                    cache,
                    name,
                    expected);
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    private async Task AssertStockScriptAsync(
        Rp6lArchive archive,
        Rp6lChunkCache cache,
        string name,
        InstalledAnimationScrControl expected)
    {
        Rp6lResourceDescriptor resource =
            archive.FindResource(
                Rp6lResourceTypes.AnimationScript,
                name)
            ?? throw new InvalidDataException(
                $"Installed animation script '{name}' was not found.");
        Assert.Equal(expected.ResourceIndex, resource.Index);
        Assert.Equal(2, resource.ItemCount);
        byte[] section0 = await archive.ReadItemBytesAsync(
            resource.Items[0],
            cache);
        byte[] section1 = await archive.ReadItemBytesAsync(
            resource.Items[1],
            cache);
        var sections = new AnimationScrSections(section0, section1);
        ParsedAnimationScr parsed = AnimationScrCodec.Parse(sections);
        uint[] markerValues = Enumerable
            .Range(0, parsed.DeclaredSequenceCount)
            .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(
                section0.AsSpan(
                    (index * AnimationScrCodec.RecordSize) + 4)))
            .Distinct()
            .Order()
            .ToArray();
        uint[] sentinelValues = Enumerable
            .Range(0, parsed.DeclaredSequenceCount)
            .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(
                section0.AsSpan(
                    (index * AnimationScrCodec.RecordSize) + 52)))
            .Distinct()
            .Order()
            .ToArray();
        int decompileNameTableOffset = checked(
            (parsed.DeclaredSequenceCount * AnimationScrCodec.RecordSize) +
            checked(
                (int)parsed.TotalDeclaredEventCount *
                AnimationScrCodec.EventRecordSize));
        string decompileNameTablePrefix = Convert.ToHexString(
            section0.AsSpan(
                decompileNameTableOffset,
                Math.Min(64, section0.Length - decompileNameTableOffset)));

        Assert.True(
            parsed.Sequences.Length > 0,
            $"{name} parsed no valid sequences; " +
            $"section0={section0.Length} bytes/" +
            $"{Convert.ToHexString(section0.AsSpan(0, Math.Min(16, section0.Length)))}, " +
            $"section1={section1.Length} bytes/" +
            $"{Convert.ToHexString(section1.AsSpan(0, Math.Min(16, section1.Length)))}, " +
            $"chunks={string.Join(',', resource.Items.Select(item => archive.Chunks[item.ChunkIndex].Category))}, " +
            $"declared={parsed.DeclaredSequenceCount}, " +
            $"name-table={parsed.NameTableOffset}, " +
            $"events={parsed.TotalDeclaredEventCount}, " +
            $"event-bytes={parsed.OpaquePayloadLength}/" +
            $"{parsed.ExpectedEventTableLength}, " +
            $"decompile-name-table={decompileNameTableOffset}/" +
            $"{decompileNameTablePrefix}, " +
            $"markers={string.Join(',', markerValues)}, " +
            $"sentinels={string.Join(',', sentinelValues)}.");
        Assert.Equal(
            parsed.DeclaredSequenceCount,
            parsed.Sequences.Length);
        Assert.Equal(
            expected.SequenceCount,
            parsed.DeclaredSequenceCount);
        Assert.Equal(
            expected.EventCount,
            parsed.TotalDeclaredEventCount);
        Assert.True(parsed.TotalDeclaredEventCount > 0);
        Assert.NotNull(parsed.ExpectedEventTableLength);
        Assert.Equal(
            parsed.ExpectedEventTableLength,
            parsed.OpaquePayloadLength);
        Assert.True(parsed.HasCanonicalEventTableLayout);
        Assert.Equal(
            expected.EventTableLength,
            parsed.OpaquePayloadLength);
        Assert.Equal(
            parsed.DeclaredSequenceCount * AnimationScrCodec.RecordSize,
            parsed.OpaquePayloadOffset);
        Assert.Equal(
            AnimationScrCodec.Retail155RecordMagic,
            Assert.Single(markerValues));
        Assert.Equal(
            AnimationScrCodec.Retail155RecordSentinel,
            Assert.Single(sentinelValues));

        ParsedAnimationScrSequence patchTarget =
            parsed.Sequences.First(static sequence =>
                float.IsFinite(sequence.StartFrame) &&
                float.IsFinite(sequence.EndFrame) &&
                float.IsFinite(sequence.FramesPerSecond) &&
                sequence.StartFrame >= 0 &&
                sequence.EndFrame >= sequence.StartFrame &&
                sequence.FramesPerSecond > 0);
        float replacementFps =
            patchTarget.FramesPerSecond == 30
                ? 31
                : 30;
        AnimationScrSections patched = AnimationScrCodec.PatchRanges(
            sections,
            new Dictionary<
                string,
                (float Start, float End, float FramesPerSecond)>
            {
                [patchTarget.Name] =
                    (
                        patchTarget.StartFrame,
                        patchTarget.EndFrame,
                        replacementFps),
            });

        Assert.Equal(section1, patched.IndexAndNames);
        Assert.Equal(
            section0
                .AsSpan(
                    parsed.OpaquePayloadOffset,
                    parsed.OpaquePayloadLength)
                .ToArray(),
            patched.RecordsAndNames
                .AsSpan(
                    parsed.OpaquePayloadOffset,
                    parsed.OpaquePayloadLength)
                .ToArray());
        int[] changedOffsets = section0
            .Select(
                (value, index) =>
                    value == patched.RecordsAndNames[index]
                        ? -1
                        : index)
            .Where(static index => index >= 0)
            .ToArray();
        Assert.NotEmpty(changedOffsets);
        Assert.All(
            changedOffsets,
            offset => Assert.InRange(
                offset,
                patchTarget.RecordOffset + 24,
                patchTarget.RecordOffset + 35));

        _output.WriteLine(
            $"{name}: resource={resource.Index}, " +
            $"sequences={parsed.DeclaredSequenceCount}, " +
            $"events={parsed.TotalDeclaredEventCount}, " +
            $"event-bytes={parsed.OpaquePayloadLength}");
    }

    private sealed record InstalledAnimationScrControl(
        int ResourceIndex,
        int SequenceCount,
        ulong EventCount,
        int EventTableLength);
}

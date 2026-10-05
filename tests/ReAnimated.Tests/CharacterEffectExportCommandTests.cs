using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Cli;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

[Collection("Character CLI")]
public sealed class CharacterEffectExportCommandTests
{
    private static readonly string[] ExpectedNames = ["child", "effects/parent"];
    private static readonly sbyte[] ExpectedKinds = [127, -128];
    [Theory]
    [InlineData("none", Rp6lCompression.None)]
    [InlineData("zlib", Rp6lCompression.Zlib)]
    public async Task ExportPreservesEveryRequiredDefinitionAndUnknownSignedKinds(string option, Rp6lCompression compression)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var package = CreatePackage();
            string input = Path.Combine(directory, "character.dlrmodel");
            string output = Path.Combine(directory, "effects.rpack");
            CustomModelPackageSerializer.SaveAtomic(package, input);
            byte[] before = await File.ReadAllBytesAsync(input);
            string[] cacheBefore = Directory.GetDirectories(Path.GetTempPath(), "dl-reanimated-character-effects-*");
            string[] args = ["character", "export-effects", input, "--output", output, "--compression", option];

            var result = await RunAsync(args);

            Assert.Equal(0, result.ExitCode);
            using JsonDocument report = JsonDocument.Parse(result.Output);
            Assert.Equal("dl-reanimated-character-effect-export-v1", report.RootElement.GetProperty("format").GetString());
            Assert.Equal(2, report.RootElement.GetProperty("definitionCount").GetInt32());
            Assert.Equal(1, report.RootElement.GetProperty("sourceBundleCount").GetInt32());
            Assert.Equal(Sha(await File.ReadAllBytesAsync(output)), report.RootElement.GetProperty("archiveSha256").GetString());
            Assert.Equal("FX", report.RootElement.GetProperty("resource").GetProperty("name").GetString());
            Assert.Equal((int)Rp6lResourceTypes.Effect, report.RootElement.GetProperty("resource").GetProperty("resourceType").GetInt32());
            Assert.DoesNotContain(directory, result.Output, StringComparison.OrdinalIgnoreCase);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "readback") });
            var readback = await Rp6lEffectBundleWriter.ReadBackAsync(output, cache);
            Assert.Equal(compression, readback.Chunk.Compression);
            Assert.Equal(readback.PayloadSha256, report.RootElement.GetProperty("payloadSha256").GetString());
            Assert.Equal(readback.ArchiveByteLength, report.RootElement.GetProperty("archiveByteLength").GetInt64());
            Assert.Equal(ExpectedNames, readback.Definitions.Select(definition => definition.Name));
            Assert.Equal(ExpectedKinds, readback.Definitions.Select(definition => definition.Kind));
            foreach (var definition in readback.Definitions)
            {
                var source = package.Document.CharacterResources!.Resources.Single(resource =>
                    resource.LogicalName == definition.Name + ".fx");
                Assert.Equal(Encoding.UTF8.GetString(package.CompanionPayloads[source.EntryPath!].AsSpan()), definition.SourceText);
                Assert.Equal(source.ContentSha256, definition.ContentSha256);
            }
            Assert.DoesNotContain(readback.Definitions, definition => definition.Name == "effects/optional");
            Assert.Equal(before, await File.ReadAllBytesAsync(input));
            var reopened = CustomModelPackageSerializer.Load(input);
            Assert.Equal(JsonSerializer.Serialize(package.Document.CharacterResources), JsonSerializer.Serialize(reopened.Document.CharacterResources));
            byte[] created = await File.ReadAllBytesAsync(output);
            Assert.Equal(2, (await RunAsync(args)).ExitCode);
            Assert.Equal(created, await File.ReadAllBytesAsync(output));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.Equal(cacheBefore.Order(StringComparer.Ordinal),
                Directory.GetDirectories(Path.GetTempPath(), "dl-reanimated-character-effects-*").Order(StringComparer.Ordinal));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData("missing-nested")]
    [InlineData("missing-record")]
    [InlineData("unsupported-record")]
    [InlineData("ambiguous-record")]
    [InlineData("no-receipt")]
    [InlineData("kind")]
    [InlineData("source-text")]
    [InlineData("parent-bytes")]
    [InlineData("case-duplicate")]
    [InlineData("malformed-source")]
    public async Task InvalidPackedSourcesOrClosureCreateNoOutputAndPreserveInput(string scenario)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var package = CreatePackage(missingNested: scenario == "missing-nested",
                malformedSource: scenario == "malformed-source");
            var inventory = package.Document.CharacterResources!;
            var parentEffect = inventory.Resources.Single(resource => resource.Id == "effect-parent");
            if (scenario is "missing-record" or "unsupported-record" or "ambiguous-record" or "no-receipt")
            {
                CharacterDependencyStatus status = scenario switch
                {
                    "missing-record" => CharacterDependencyStatus.Missing,
                    "unsupported-record" => CharacterDependencyStatus.Unsupported,
                    "ambiguous-record" => CharacterDependencyStatus.Ambiguous,
                    _ => parentEffect.Status,
                };
                package = package with { Document = package.Document with { CharacterResources = inventory with
                {
                    Resources = inventory.Resources.Select(resource => resource.Id == parentEffect.Id
                        ? resource with { Status = status, PackedEffect = scenario == "no-receipt" ? null : resource.PackedEffect } : resource).ToImmutableArray(),
                } } };
            }
            if (scenario == "case-duplicate")
            {
                package = package with { Document = package.Document with { CharacterResources = inventory with
                {
                    Resources = inventory.Resources.Add(parentEffect with
                    { Id = "case-alias", Required = true, LogicalName = parentEffect.LogicalName.ToUpperInvariant(),
                        EntryPath = "character/resources/case-alias.bin", PackedEffect = null }),
                } }, CompanionPayloads = package.CompanionPayloads.Add("character/resources/case-alias.bin",
                    package.CompanionPayloads[parentEffect.EntryPath!]) };
            }
            string input = Path.Combine(directory, "character.dlrmodel");
            string output = Path.Combine(directory, "effects.rpack");
            CustomModelPackageSerializer.SaveAtomic(package, input);
            if (scenario == "kind")
                PatchManifest(input, json => json["characterResources"]!["resources"]!.AsArray()
                    .Single(row => row!["id"]!.GetValue<string>() == parentEffect.Id)!["packedEffect"]!["kind"] = 6);
            if (scenario is "source-text" or "parent-bytes")
            {
                string entry = scenario == "source-text" ? parentEffect.EntryPath! :
                    inventory.Resources.Single(resource => resource.Id == "effect-bundle").EntryPath!;
                using var file = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using var archive = new ZipArchive(file, ZipArchiveMode.Update);
                var existing = archive.GetEntry(entry)!;
                byte[] bytes;
                using (var stream = existing.Open())
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    bytes = memory.ToArray();
                }
                bytes[0] ^= 1;
                existing.Delete();
                using var changed = archive.CreateEntry(entry).Open();
                changed.Write(bytes);
            }
            byte[] before = await File.ReadAllBytesAsync(input);

            var result = await RunAsync(["character", "export-effects", input, "--output", output]);

            Assert.Equal(2, result.ExitCode);
            Assert.NotEmpty(result.Error);
            Assert.False(File.Exists(output));
            Assert.Equal(before, await File.ReadAllBytesAsync(input));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task CancellationAndInvalidOptionsProduceNoOutput()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string input = Path.Combine(directory, "character.dlrmodel");
            string output = Path.Combine(directory, "effects.rpack");
            CustomModelPackageSerializer.SaveAtomic(CreatePackage(), input);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            string[] args = ["character", "export-effects", input, "--output", output];
            Assert.Equal(130, (await RunAsync(args, cancellation.Token)).ExitCode);
            Assert.Equal(2, (await RunAsync(args.Concat(["--compression", "lzma"]).ToArray())).ExitCode);
            Assert.Equal(2, (await RunAsync(args.Concat(["--output", output]).ToArray())).ExitCode);
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    internal static CustomModelPackage CreatePackage(bool missingNested = false, bool malformedSource = false)
    {
        var package = ModelsWorkspaceMorphAuthoringTests.CreateGenericDifferentTopologyTargetPackage();
        using var bytes = new MemoryStream();
        Add("effects/parent", -128, malformedSource ? "Broken(\"child.fx\"" :
            "FutureKind(17); Nested(\"child.fx\"); // ignored \"unused.fx\"\n");
        Add("child", 127, "FutureKind(19); Label(\"generic-token\");\n");
        Add("effects/optional", 6, "Optional();\n");
        bytes.WriteByte(0);
        byte[] payload = bytes.ToArray();
        string bundleHash = Sha(payload);
        var records = ImmutableArray.CreateBuilder<CharacterResourceRecord>();
        var payloads = package.CompanionPayloads;
        var bundle = new CharacterResourceRecord
        {
            Id = "effect-bundle", LogicalName = "original-effects.bin",
            EntryPath = "character/resources/original-effects.bin",
            ProviderIdentity = "generic-rpack", SourceFingerprint = new string('a', 64),
            ContentSha256 = bundleHash, ByteLength = payload.Length,
            Subsystem = CharacterSubsystem.Damage, Status = CharacterDependencyStatus.Preserved,
            Required = false, IsOriginalArchive = true,
        };
        records.Add(bundle);
        payloads = payloads.Add(bundle.EntryPath, ImmutableArray.Create(payload));
        foreach (var definition in Rp6lEffectBundleDecoder.Decode(payload))
        {
            if (missingNested && definition.Name == "child") continue;
            string id = "effect-" + definition.Name.Split('/')[^1];
            string entry = "character/resources/" + id + ".bin";
            byte[] text = Encoding.UTF8.GetBytes(definition.SourceText);
            records.Add(new()
            {
                Id = id, LogicalName = definition.Name + ".fx", EntryPath = entry,
                ProviderIdentity = "generic-rpack-effects", SourceFingerprint = new string('a', 64),
                ContentSha256 = definition.ContentSha256, ByteLength = text.Length,
                Subsystem = CharacterSubsystem.Damage, Status = CharacterDependencyStatus.Preserved,
                Required = definition.Name != "effects/optional",
                PackedEffect = new()
                {
                    BundleResourceId = bundle.Id, BundleSha256 = bundleHash, StoredName = definition.Name,
                    Kind = definition.Kind, ResourceIndex = 0, ItemIndex = 0, ChunkIndex = 0,
                    EntryOffset = definition.EntryOffset, EntryByteLength = definition.EntryByteLength,
                    TextOffset = definition.TextOffset, TextByteLength = definition.TextByteLength,
                },
            });
            payloads = payloads.Add(entry, ImmutableArray.Create(text));
        }
        return package with
        {
            Document = package.Document with { CharacterResources = package.Document.CharacterResources! with
            { Resources = package.Document.CharacterResources.Resources.AddRange(records) } },
            CompanionPayloads = payloads,
        };

        void Add(string name, sbyte kind, string text)
        {
            bytes.Write(Encoding.UTF8.GetBytes(name));
            bytes.WriteByte(0);
            bytes.WriteByte(unchecked((byte)kind));
            bytes.Write(Encoding.UTF8.GetBytes(text));
            bytes.WriteByte(0);
        }
    }

    private static void PatchManifest(string path, Action<System.Text.Json.Nodes.JsonObject> change)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Update);
        var entry = archive.GetEntry(CustomModelPackage.ManifestEntryPath)!;
        System.Text.Json.Nodes.JsonObject manifest;
        using (var stream = entry.Open())
            manifest = System.Text.Json.Nodes.JsonNode.Parse(stream)!.AsObject();
        change(manifest);
        entry.Delete();
        using var output = archive.CreateEntry(CustomModelPackage.ManifestEntryPath).Open();
        JsonSerializer.Serialize(output, manifest);
    }

    private static async Task<CliResult> RunAsync(string[] args, CancellationToken token = default)
    {
        TextWriter previousOutput = Console.Out, previousError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            return new(await CliApplication.RunAsync(args, token), output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record CliResult(int ExitCode, string Output, string Error);
}
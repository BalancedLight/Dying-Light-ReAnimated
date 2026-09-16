using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.Tests;

public sealed class Dl1RigCorpusCollectorTests
{
    private static string Build => new('a', 64);
    private static RetailAssetLogicalId FileId(string name) => RetailAssetLogicalId.VirtualFile(name);
    private static (RetailAssetLogicalId Id, byte[] Payload) Text(string path, string source) => (FileId(path), Encoding.UTF8.GetBytes(source));

    [Fact]
    public async Task CapturesWinnerAndShadowedHashesAndTraversesOnlyWinnerDependencies()
    {
        var root = Text("scripts/root.scr", "!include(\"child.scr\")");
        var shadowed = Text("scripts/root.scr", "!include(\"missing.scr\")");
        var child = Text("scripts/child.scr", "!include(\"root.scr\")\nSeqTrack(\"walk\", \"walk.anm2\", 0, 30, 30, 1, 0.5)");
        RetailAssetLogicalId clipId = RetailAssetLogicalId.Rpack(320, "walk");
        var stock = new Provider("stock", 1, shadowed);
        var project = new Provider("project", 2, root, child, (clipId, [1, 2, 3]));
        var catalog = await RetailAssetCatalog.BuildAsync([stock, project]);
        var report = await Dl1RigCorpusCollector.CollectAsync(catalog, [root.Id], Build);
        Assert.Equal(3, report.Resources.Length);
        var captured = Assert.Single(report.Resources, r => r.LogicalId == root.Id);
        Assert.Equal(2, captured.Candidates.Length);
        Assert.Equal("project", Assert.Single(captured.Candidates, c => c.SelectedByCatalog).Asset.Source.ProviderId);
        Assert.Equal(2, captured.Candidates.Select(c => c.ContentSha256).Distinct().Count());
        Assert.DoesNotContain(report.Resources, r => r.LogicalId.Name.Contains("missing", StringComparison.Ordinal));
        var clip = Assert.Single(report.Resources, r => r.LogicalId == clipId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })), clip.Candidates[0].ContentSha256);
        Assert.Equal("concatenated-readable-rpack-items-v1", clip.Candidates[0].HashDomain);
        Assert.False(report.RuntimeBindingVerified);
        Assert.False(report.DependencySemanticsComplete);
        Assert.NotEmpty(clip.Unknowns);
    }

    [Fact]
    public async Task DynamicMissingAndAmbiguousReferencesAreNotSilentlyDropped()
    {
        var root = Text("scripts/root.ascr", "!include(Choose())\n!include(\"missing.scr\")\n!include(\"duplicate.scr\")\nAnimScriptAlias(\"bank\")");
        var provider = new Provider("source", 1, root, Text("a/duplicate.scr", ""), Text("b/duplicate.scr", ""),
            (RetailAssetLogicalId.Rpack(322, "bank"), [4, 5]));
        var report = await Dl1RigCorpusCollector.CollectAsync(await RetailAssetCatalog.BuildAsync([provider]), [root.Id], Build);
        var result = Assert.Single(report.Resources, r => r.LogicalId == root.Id);
        Assert.Collection(result.Dependencies,
            d => Assert.Equal(Dl1CorpusDependencyStatus.Dynamic, d.Status),
            d => Assert.Equal(Dl1CorpusDependencyStatus.Missing, d.Status),
            d => { Assert.Equal(Dl1CorpusDependencyStatus.Ambiguous, d.Status); Assert.Equal(2, d.Candidates.Length); },
            d => Assert.Equal(Dl1CorpusDependencyStatus.UniqueCatalogMatch, d.Status));
        Assert.Equal(2, report.Resources.Length);
    }

    [Fact]
    public async Task DecodeFailuresRemainDistinctFromReadableEmptySources()
    {
        var broken = Text("scripts/broken.scr", "SeqTrack(BAD)");
        var empty = Text("scripts/empty.scr", "");
        var missing = FileId("scripts/missing.scr");
        var catalog = await RetailAssetCatalog.BuildAsync([new Provider("source", 1, broken, empty)]);
        var report = await Dl1RigCorpusCollector.CollectAsync(catalog, [broken.Id, empty.Id, missing], Build);
        var brokenRow = Assert.Single(report.Resources, r => r.LogicalId == broken.Id);
        Assert.Equal(Dl1CorpusReadStatus.Read, brokenRow.Status);
        Assert.Contains(brokenRow.Unknowns, u => u.StartsWith("Source decode incomplete:", StringComparison.Ordinal));
        Assert.Equal(Dl1CorpusReadStatus.Read, Assert.Single(report.Resources, r => r.LogicalId == empty.Id).Status);
        Assert.Equal(Dl1CorpusReadStatus.Missing, Assert.Single(report.Resources, r => r.LogicalId == missing).Status);
    }

    [Fact]
    public async Task CapturesChrVariantTransformsWithoutInventingAnimationBinding()
    {
        var chr = Dl1ChrV4Codec.CreateEditorMenuOneDefaultVariant([new("synthetic_root", TransformMatrix.Identity)]);
        var id = FileId("characters/generic.chr");
        var catalog = await RetailAssetCatalog.BuildAsync([new Provider("source", 1, (id, Dl1ChrV4Codec.Build(chr)))]);
        var report = await Dl1RigCorpusCollector.CollectAsync(catalog, [id], Build);
        var captured = Assert.Single(report.Resources).Character!;
        Assert.Equal("default", Assert.Single(captured.Variants).Name);
        Assert.Equal(TransformMatrix.Identity, Assert.Single(captured.Variants[0].ObjectTransforms));
        Assert.False(report.RuntimeBindingVerified);
    }

    [Fact]
    public async Task SymbolicTimingAndOpaqueEventsRemainVisible()
    {
        var source = Text("scripts/root.scr", "SeqTrack(\"idle\", \"idle.anm2\", 0, 10, 30, 1, BLEND_TIME) { Event(\"opaque\") }");
        var report = await Dl1RigCorpusCollector.CollectAsync(await RetailAssetCatalog.BuildAsync([new Provider("source", 1, source)]), [source.Id], Build);
        var row = Assert.Single(report.Resources);
        Assert.Equal("BLEND_TIME", Assert.Single(row.SourceTracks).Blend.Text);
        Assert.Contains(row.Unknowns, u => u.StartsWith("Symbolic", StringComparison.Ordinal));
        Assert.Contains(row.Unknowns, u => u.StartsWith("Event blocks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedSnapshotsAndReadFailuresCannotProduceAContentHash()
    {
        var source = Text("scripts/root.scr", "");
        var provider = new Provider("source", 1, source) { ExtraDeclaredBytes = 1 };
        var report = await Dl1RigCorpusCollector.CollectAsync(await RetailAssetCatalog.BuildAsync([provider]), [source.Id], Build);
        var row = Assert.Single(report.Resources);
        Assert.Equal(Dl1CorpusReadStatus.Failed, row.Status);
        Assert.Null(Assert.Single(row.Candidates).ContentSha256);
        provider = new Provider("source", 1, source) { FailRead = true };
        report = await Dl1RigCorpusCollector.CollectAsync(await RetailAssetCatalog.BuildAsync([provider]), [source.Id], Build);
        Assert.Equal(Dl1CorpusReadStatus.Failed, Assert.Single(report.Resources).Status);
    }

    [Fact]
    public async Task CancellationAndTraversalBudgetsAbortInsteadOfReturningFalseCompleteness()
    {
        var root = Text("scripts/root.scr", "!include(\"child.scr\")");
        var child = Text("scripts/child.scr", "");
        var catalog = await RetailAssetCatalog.BuildAsync([new Provider("source", 1, root, child)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Dl1RigCorpusCollector.CollectAsync(catalog, [root.Id], Build, cancellationToken: new(true)));
        await Assert.ThrowsAnyAsync<IOException>(() => Dl1RigCorpusCollector.CollectAsync(catalog, [root.Id], Build, new() { MaximumResources = 1 }));
        await Assert.ThrowsAnyAsync<IOException>(() => Dl1RigCorpusCollector.CollectAsync(catalog, [root.Id], Build, new() { MaximumTotalBytes = 1 }));
    }

    [Fact]
    public async Task InspectedEmbeddedAliasExtendsTheGraphAndKeepsPhysicalProof()
    {
        var mesh = RetailAssetLogicalId.Rpack(272, "synthetic");
        var bank = RetailAssetLogicalId.Rpack(322, "synthetic_bank");
        var catalog = await RetailAssetCatalog.BuildAsync([new Provider("source", 1, (mesh, [1]), (bank, [2]))]);
        var report = await Dl1RigCorpusCollector.CollectAsync(catalog, [mesh], Build, inspectCompiled: (asset, digest, _) =>
            Task.FromResult(new Dl1CompiledCorpusEvidence(asset.Id, digest, [], null,
                asset.Id.LogicalId == mesh ? "synthetic_bank.scr" : null, [], [], null, ["Synthetic decoder observation."])));
        Assert.Equal(2, report.Resources.Length);
        var row = Assert.Single(report.Resources, r => r.LogicalId == mesh);
        Assert.NotNull(row.Compiled);
        var edge = Assert.Single(row.Dependencies);
        Assert.Equal("CompiledAnimationScriptAlias", edge.Kind);
        Assert.Equal(bank, Assert.Single(edge.Candidates));
        Assert.False(report.RuntimeBindingVerified);

        var mismatched = await Dl1RigCorpusCollector.CollectAsync(catalog, [mesh], Build, inspectCompiled: (asset, _, _) =>
            Task.FromResult(new Dl1CompiledCorpusEvidence(asset.Id, new string('a', 64), [], null, "synthetic_bank.scr", [], [], null, [])));
        Assert.Null(Assert.Single(mismatched.Resources).Compiled);
        Assert.Empty(mismatched.Resources[0].Dependencies);
        Assert.Contains(mismatched.Resources[0].Unknowns, u => u.StartsWith("Compiled inspection incomplete:", StringComparison.Ordinal));
    }

    private sealed class Provider(string id, int priority, params (RetailAssetLogicalId Id, byte[] Payload)[] entries) : IRetailAssetProvider
    {
        public string ProviderId => id;
        public int ExtraDeclaredBytes { get; init; }
        public bool FailRead { get; init; }
        public async IAsyncEnumerable<RetailAssetRecord> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            for (int i = 0; i < entries.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[i];
                yield return new(RetailAssetId.Create(entry.Id, "synthetic-install", id, i, priority, "synthetic-snapshot"), entry.Id.Name,
                    new(id, RetailAssetSourceKind.LooseFile, priority, "synthetic-container", entry.Id.Name, null,
                        entry.Payload.Length + ExtraDeclaredBytes, entry.Payload.Length, DateTime.UnixEpoch));
            }
        }
        public ValueTask<Stream> OpenReadAsync(RetailAssetRecord asset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailRead) throw new IOException("Synthetic read failure.");
            return ValueTask.FromResult<Stream>(new MemoryStream(entries[(int)asset.Id.SourceIndex].Payload, writable: false));
        }
    }
}

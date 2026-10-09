using System.Collections.Immutable;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Materials;

namespace ReAnimated.Tests;

public sealed class Dl1CharacterImportTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task SyntheticCharacterImportPreservesMorphsSurfaceAndLodMetadata()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        FbxModelAuthoringImportResult imported = await Dl1CharacterImporter.ImportAsync(mesh, root, new FakeCatalog(root));
        Assert.Equal("smile", Assert.Single(imported.Package.Document.MorphChannels).Name);
        Assert.Equal(2, imported.Surfaces.Length);
        Assert.Contains(imported.Surfaces, s => s.Id == "1/1/0");
        Assert.Contains(imported.Surfaces, s => s.MorphTargets.Any(m => m.Name == "smile" && m.PositionDeltas.Any(d => d.X != 0)));
        Assert.True(imported.Surfaces[0].PaletteBoneIndices.SequenceEqual([1]));
        Assert.All(imported.Surfaces[0].Vertices, v => Assert.True(v.BoneIndices.SequenceEqual([0])));
        Assert.Equal(2, imported.Rig!.BoneCount);
        Assert.Equal(["root", "face"], imported.Rig.Bones.Select(b => b.Name));
        Assert.Equal(new Vector3D(1, 1, 1), imported.Package.Document.Bones[1].LocalBounds!.Value.HalfExtents);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task IncompleteMeshIsRejectedBeforeDependencyAccess()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => Dl1CharacterImporter.ImportAsync(mesh with { Rig = null }, root, new FakeCatalog(root)));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ExpressionKeepReplaceAndZeroPlaceholderPoliciesAreEnforced()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        FbxModelAuthoringImportResult imported = await Dl1CharacterImporter.ImportAsync(mesh, root, new FakeCatalog(root));
        string surface = imported.Surfaces[0].Id;
        var deltas = ImmutableArray.CreateRange(Enumerable.Repeat(new ReAnimated.Core.Mathematics.Vector3D(.2, 0, 0), imported.Surfaces[0].Vertices.Length));
        FbxModelAuthoringImportResult added = CharacterGeometryAuthoring.SetExpression(imported, surface, "new_face", 0x77889900, deltas, MorphTransferConflict.Reject, reviewed: true);
        Assert.Throws<InvalidOperationException>(() => CharacterGeometryAuthoring.SetExpression(added, surface, "new_face", 0x77889900, deltas, MorphTransferConflict.Reject, true));
        Assert.Same(added, CharacterGeometryAuthoring.SetExpression(added, surface, "new_face", 0x77889900, deltas, MorphTransferConflict.KeepExisting, true));
        Assert.Throws<InvalidDataException>(() => CharacterGeometryAuthoring.SetExpression(added, surface, "other", 0x11223344, ImmutableArray.CreateRange(Enumerable.Repeat(ReAnimated.Core.Mathematics.Vector3D.Zero, imported.Surfaces[0].Vertices.Length)), MorphTransferConflict.Reject, true));
        FbxModelAuthoringImportResult replaced = CharacterGeometryAuthoring.SetExpression(added, surface, "new_face", 0x77889900, deltas.Select(d => d with { X = .4 }).ToImmutableArray(), MorphTransferConflict.ReplaceExisting, true);
        Assert.Equal(.4, replaced.Surfaces[0].MorphTargets.Single(m => m.Name == "new_face").PositionDeltas[0].X);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task PackageRoundTripPreservesMorphSurfaceAndSourceCustody()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        FbxModelAuthoringImportResult imported = await Dl1CharacterImporter.ImportAsync(mesh, root, new FakeCatalog(root));
        ImmutableArray<byte> bytes = CustomModelPackageSerializer.Serialize(imported.Package);
        string path = Path.Combine(Path.GetTempPath(), "dlra-character-roundtrip-" + Guid.NewGuid().ToString("N") + ".dlrmodel");
        try
        {
            await File.WriteAllBytesAsync(path, bytes.ToArray());
            CustomModelPackage loaded = CustomModelPackageSerializer.Load(path);
            Assert.Equal(imported.Package.Document.MorphSignature, loaded.Document.MorphSignature);
            Assert.Equal(imported.Package.Document.Meshes.Select(m => m.Name), loaded.Document.Meshes.Select(m => m.Name));
            Assert.Equal(imported.Package.Document.Meshes.Select(m => m.ExpandedVertexCount), loaded.Document.Meshes.Select(m => m.ExpandedVertexCount));
            Assert.True(imported.Package.SourceFbx.SequenceEqual(loaded.SourceFbx));
            Assert.Equal(imported.Package.Document.MorphChannels.Select(m => (m.Name, m.DescriptorHash)), loaded.Document.MorphChannels.Select(m => (m.Name, m.DescriptorHash)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task PackageSourceTamperingIsRejectedByLoad()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        FbxModelAuthoringImportResult imported = await Dl1CharacterImporter.ImportAsync(mesh, root, new FakeCatalog(root));
        byte[] bytes = CustomModelPackageSerializer.Serialize(imported.Package).ToArray();
        string path = Path.Combine(Path.GetTempPath(), "dlra-character-tamper-" + Guid.NewGuid().ToString("N") + ".dlrmodel");
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            string temp = path + ".tmp";
            using (ZipArchive input = ZipFile.OpenRead(path))
            using (ZipArchive output = ZipFile.Open(temp, ZipArchiveMode.Create))
            foreach (ZipArchiveEntry entry in input.Entries)
            {
                ZipArchiveEntry next = output.CreateEntry(entry.FullName);
                using Stream source = entry.Open(); using Stream destination = next.Open();
                source.CopyTo(destination);
            }
            using (ZipArchive archive = ZipFile.Open(temp, ZipArchiveMode.Update))
            {
                ZipArchiveEntry source = archive.GetEntry(imported.Package.Document.Source.EmbeddedEntryPath)!;
                byte[] changed;
                using (Stream stream = source.Open())
                using (var copy = new MemoryStream()) { stream.CopyTo(copy); changed = copy.ToArray(); }
                changed[0] ^= 0xFF;
                source.Delete();
                ZipArchiveEntry replacement = archive.CreateEntry(imported.Package.Document.Source.EmbeddedEntryPath);
                using Stream write = replacement.Open(); write.Write(changed);
            }
            File.Move(temp, path, true);
            Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.Load(path));
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task AttachmentWithConflictingMaterialIdentityIsRejectedWithoutMutation()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        FbxModelAuthoringImportResult character = await Dl1CharacterImporter.ImportAsync(mesh, root, new FakeCatalog(root));
        Assert.Throws<InvalidDataException>(() => CharacterGeometryAuthoring.AddAttachment(character, character));
        Assert.Equal(2, character.Surfaces.Length);
        Assert.Equal(2, character.Package.Document.Meshes.Length);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task RigidAttachmentUsesLocalPaletteIndexesAndTargetGlobalPalette()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        FbxModelAuthoringImportResult character = await Dl1CharacterImporter.ImportAsync(mesh, root, new FakeCatalog(root));
        Guid attachmentMaterialId = Guid.NewGuid();
        FbxModelAuthoringImportResult attachment = character with
        {
            Surfaces = [character.Surfaces[0] with { Id = "attachment/face", MeshName = "attachment_face", MaterialId = attachmentMaterialId }],
            Package = character.Package with
            {
                Document = character.Package.Document with
                {
                    Materials = [character.Package.Document.Materials[0] with { Id = attachmentMaterialId, Name = "attachment_material" }],
                    Meshes = [character.Package.Document.Meshes[0] with { Name = "attachment_face" }],
                },
            },
            SourceCharacterLods = [],
            SourceLodGroups = [],
        };
        FbxModelAuthoringImportResult result = CharacterGeometryAuthoring.AddAttachment(character, attachment, "face");
        FbxModelSurface appended = Assert.Single(result.Surfaces.Where(s => s.Id.StartsWith("attachment:", StringComparison.Ordinal)));
        Assert.True(appended.PaletteBoneIndices.SequenceEqual([1]));
        Assert.All(appended.Vertices, v => Assert.True(v.BoneIndices.SequenceEqual([0])));
        Assert.All(appended.Vertices, v => Assert.True(v.BoneWeights.SequenceEqual([1.0])));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task GeometryRevisionRoundTripsSurfaceMorphAndLodData()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        FbxModelAuthoringImportResult imported = await Dl1CharacterImporter.ImportAsync(mesh, root, new FakeCatalog(root));
        FbxModelAuthoringImportResult captured = ModelGeometryRevisionCodec.Capture(imported);
        string path = Path.Combine(Path.GetTempPath(), "dlra-character-revision-" + Guid.NewGuid().ToString("N") + ".dlrmodel");
        try
        {
            await File.WriteAllBytesAsync(path, CustomModelPackageSerializer.Serialize(captured.Package).ToArray());
            CustomModelPackage loaded = CustomModelPackageSerializer.Load(path);
            FbxModelAuthoringImportResult replayed = ModelGeometryRevisionCodec.Replay(imported, loaded);
            Assert.Equal(captured.Surfaces.Select(s => s.Id), replayed.Surfaces.Select(s => s.Id));
            Assert.Equal(captured.Surfaces.SelectMany(s => s.Vertices).Select(v => v.Position), replayed.Surfaces.SelectMany(s => s.Vertices).Select(v => v.Position));
            Assert.Equal(captured.Surfaces.SelectMany(s => s.MorphTargets).SelectMany(m => m.PositionDeltas), replayed.Surfaces.SelectMany(s => s.MorphTargets).SelectMany(m => m.PositionDeltas));
            Assert.Equal(captured.SourceCharacterLods.Select(l => l.Name), replayed.SourceCharacterLods.Select(l => l.Name));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ClonedPackageModelIdCanReopenSharedSourceSnapshotAndRevision()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        FbxModelAuthoringImportResult imported = await Dl1CharacterImporter.ImportAsync(mesh, root, new FakeCatalog(root));
        FbxModelAuthoringImportResult captured = ModelGeometryRevisionCodec.Capture(imported);
        CustomModelDocument clonedDocument = captured.Package.Document with { ModelId = Guid.NewGuid() };
        CustomModelPackage cloned = captured.Package with { Document = clonedDocument };
        string path = Path.Combine(Path.GetTempPath(), "dlra-character-clone-" + Guid.NewGuid().ToString("N") + ".dlrmodel");
        try
        {
            await File.WriteAllBytesAsync(path, CustomModelPackageSerializer.Serialize(cloned).ToArray());
            CustomModelPackage reopened = CustomModelPackageSerializer.Load(path);
            FbxModelAuthoringImportResult replayed = ModelGeometryRevisionCodec.Replay(imported, reopened);
            Assert.Equal(clonedDocument.ModelId, replayed.Package.Document.ModelId);
            Assert.Equal(captured.Surfaces.SelectMany(s => s.Vertices).Select(v => v.Position), replayed.Surfaces.SelectMany(s => s.Vertices).Select(v => v.Position));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task PerspectiveExportFiltersSelectedTrianglesWithoutMutatingOriginalOrLodInventory()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        FbxModelAuthoringImportResult imported = await Dl1CharacterImporter.ImportAsync(mesh, root, new FakeCatalog(root));
        FbxModelSurface sourceSurface = imported.Surfaces[0];
        CustomModelPerspectiveSelection selection = new()
        {
            Perspective = CustomModelPerspective.FirstPerson,
            SourceSha256 = imported.Package.Document.Source.ContentSha256,
            SourceGeometryFingerprint = FbxModelPerspectiveAuthoring.ComputeGeometryFingerprint(imported),
            HiddenTriangles = [new CustomModelPerspectiveTriangleKey(sourceSurface.SourceGeometry!.Id, sourceSurface.SourceTriangles[0])],
        };
        Assert.Throws<InvalidDataException>(() => FbxModelPerspectiveAuthoring.CreateFirstPersonExportView(imported, selection));
        Assert.Equal(2, imported.Surfaces.Length);
        Assert.Equal(imported.SourceCharacterLods.Select(l => l.Name), imported.SourceCharacterLods.Select(l => l.Name));

        CustomModelPerspectiveSelection keepAll = selection with { HiddenTriangles = [] };
        FbxModelPerspectiveExportView unchanged = FbxModelPerspectiveAuthoring.CreateFirstPersonExportView(imported, keepAll);
        Assert.Equal(2, unchanged.Model.Surfaces.Length);
        Assert.Equal(imported.SourceCharacterLods.Select(l => l.Name), unchanged.Model.SourceCharacterLods.Select(l => l.Name));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task MalformedGeometryRevisionPayloadIsRejectedBeforeReplay()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        FbxModelAuthoringImportResult imported = await Dl1CharacterImporter.ImportAsync(mesh, root, new FakeCatalog(root));
        FbxModelAuthoringImportResult captured = ModelGeometryRevisionCodec.Capture(imported);
        ImmutableArray<byte> changed = captured.Package.GeometryRevisionPayload.SetItem(0, (byte)(captured.Package.GeometryRevisionPayload[0] ^ 0xFF));
        CustomModelPackage tampered = captured.Package with { GeometryRevisionPayload = changed };
        Assert.Throws<InvalidDataException>(() => ModelGeometryRevisionCodec.Replay(imported, tampered));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ReservedManifestPathCannotAliasSourcePayload()
    {
        (RetailAssetRecord root,Dl1MeshData mesh)=Fixture();
        var model=await Dl1CharacterImporter.ImportAsync(mesh,root,new FakeCatalog(root));
        var package=model.Package with {Document=model.Package.Document with {Source=model.Package.Document.Source with {EmbeddedEntryPath=CustomModelPackage.ManifestEntryPath}}};
        Assert.Throws<CustomModelFormatException>(()=>CustomModelPackageSerializer.Serialize(package));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ChangedCompanionBytesCannotRetainOriginalHash()
    {
        (RetailAssetRecord root,Dl1MeshData mesh)=Fixture();
        var model=await Dl1CharacterImporter.ImportAsync(mesh,root,new FakeCatalog(root));
        var original=model.Package.CompanionPayloads.First();
        var altered=original.Value.SetItem(0,(byte)(original.Value[0]^0x01));
        Assert.Throws<CustomModelFormatException>(()=>CustomModelPackageSerializer.Serialize(model.Package with {CompanionPayloads=model.Package.CompanionPayloads.SetItem(original.Key,altered)}));
        Assert.Equal(original.Value,model.Package.CompanionPayloads[original.Key]);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void SchemaSevenMigrationRetainsAuthoredLayerAndSourceIdentity()
    {
        var source=new CustomModelSourceIdentity {OriginalFileName="synthetic.fbx",ContentSha256=new string('a',64)};
        var layer=new AuthoredModelLayerReference {ContentSha256=new string('b',64),PayloadLength=128};
        var legacy=new CustomModelDocument {SchemaVersion=7,Source=source,AuthoredLayer=layer};
        var current=CustomModelPackageSerializer.MigrateToCurrent(legacy);
        Assert.Equal(CustomModelDocument.CurrentSchemaVersion,current.SchemaVersion);
        Assert.Same(layer,current.AuthoredLayer);
        Assert.Same(source,current.Source);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task GlobalMimicSourceIsPreservedAsFacialDataWithoutClaimingActorAcceptance()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        var logical = RetailAssetLogicalId.VirtualFile("data/facemimic.scr");
        var mimic = new RetailAssetRecord(
            RetailAssetId.Create(logical, "synthetic", "synthetic", 2, 1, "snapshot", null),
            "data/facemimic.scr", new("synthetic", RetailAssetSourceKind.ZipPak, 1,
                "synthetic.pak", "data/facemimic.scr", 2, 11, 11, DateTime.UnixEpoch));
        var imported = await Dl1CharacterImporter.ImportAsync(mesh, root, new FakeCatalog(root, mimic),
            new() { CompanionRoots = [logical] });
        var retained = Assert.Single(imported.Package.Document.CharacterResources!.Resources,
            r => r.Id == logical.StableKey);
        Assert.Equal(CharacterSubsystem.FacialDefinitions, retained.Subsystem);
        Assert.Equal(CharacterDependencyStatus.Preserved, retained.Status);
        Assert.False(imported.Package.Document.CharacterResources.IsGameReady);
        Assert.Equal(CharacterDependencyStatus.Ambiguous,
            Assert.Single(imported.Package.Document.CharacterResources.Subsystems,
                r => r.Subsystem == CharacterSubsystem.FacialDefinitions).Status);
        Assert.Contains(imported.Package.Document.CharacterResources.ExportBlockers,
            b => b.StartsWith("FacialDefinitions:", StringComparison.Ordinal));
        Assert.Equal("Setting(1);", System.Text.Encoding.UTF8.GetString(
            imported.Package.CompanionPayloads[retained.EntryPath!].AsSpan()));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task PresetInventoryRetainsAllSourceBytesWithoutFollowingOtherActorFields()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        const string text = "PresetDef(\"Character\") { Preset(\"ActorA\") { SetField(\"MeshName\", \"synthetic_character.msh\"); Unknown(7); } " +
            "Preset(\"ActorB\") { SetField(\"MeshName\", \"other.msh\"); SetField(\"PhysicsScript\", \"other.phx\"); } }";
        var preset = VirtualFile("data/presets/actors.pre", text, 2, "same-source");
        var catalog = new FakeCatalog(root, preset);
        catalog.Payloads.Add(preset.Id.StableKey, Encoding.UTF8.GetBytes(text));
        var imported = await Dl1CharacterImporter.ImportAsync(mesh, root, catalog,
            new() { CompanionRoots = [preset.Id.LogicalId] });
        var inventory = imported.Package.Document.CharacterResources!;
        var retained = Assert.Single(inventory.Resources, row => row.Id == preset.Id.LogicalId.StableKey);
        Assert.Equal(CharacterSubsystem.Helpers, retained.Subsystem);
        Assert.Equal(CharacterDependencyStatus.Preserved, retained.Status);
        Assert.Equal(Encoding.UTF8.GetBytes(text), imported.Package.CompanionPayloads[retained.EntryPath!].ToArray());
        Assert.DoesNotContain(inventory.Resources, row => row.LogicalName == "other.phx" || row.LogicalName == "other.msh");
        Assert.Empty(inventory.ActorSourceReviews);
        Assert.False(inventory.IsDependencyComplete);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ParentRelativeIncludeRetainsExactCanonicalSourceAndOriginalQuotedBytes()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        const string provider = "same-source";
        const string actorName = "data/characters/group/actor.bel";
        const string actorText = "BodyElement(Head, 1, 0, 0, 0, \"root\"); !include(\"..\\..\\surface.def\");";
        const string surfaceText = "Setting(7);";
        RetailAssetRecord actor = VirtualFile(actorName, actorText, 2, provider);
        RetailAssetRecord exact = VirtualFile("data/surface.def", surfaceText, 3, provider);
        RetailAssetRecord elsewhere = VirtualFile("other/surface.def", "Setting(99);", 4, "other-source");
        var catalog = new FakeCatalog(root, actor, exact, elsewhere);
        catalog.Payloads.Add(actor.Id.StableKey, Encoding.UTF8.GetBytes(actorText));
        catalog.Payloads.Add(exact.Id.StableKey, Encoding.UTF8.GetBytes(surfaceText));
        catalog.Payloads.Add(elsewhere.Id.StableKey, Encoding.UTF8.GetBytes("Setting(99);"));

        FbxModelAuthoringImportResult imported = await Dl1CharacterImporter.ImportAsync(mesh, root, catalog,
            new() { CompanionRoots = [actor.Id.LogicalId] });

        CharacterResourceInventory inventory = imported.Package.Document.CharacterResources!;
        CharacterResourceRecord parent = Assert.Single(inventory.Resources, resource => resource.Id == actor.Id.LogicalId.StableKey);
        CharacterResourceRecord child = Assert.Single(inventory.Resources, resource => resource.Id == exact.Id.LogicalId.StableKey);
        Assert.Equal("data/surface.def", child.LogicalName);
        Assert.Equal(exact.Id.ProviderId, child.ProviderIdentity);
        Assert.Equal(Sha(surfaceText), child.ContentSha256);
        Assert.Contains(parent.Id, child.ReferencedBy);
        Assert.Equal(actorText, Encoding.UTF8.GetString(imported.Package.CompanionPayloads[parent.EntryPath!].AsSpan()));
        Assert.Equal(surfaceText, Encoding.UTF8.GetString(imported.Package.CompanionPayloads[child.EntryPath!].AsSpan()));
        Assert.DoesNotContain(inventory.Resources, resource => resource.Id == elsewhere.Id.LogicalId.StableKey);
    }

    [Theory]
    [InlineData("..\\..\\..\\..\\surface.def", CharacterDependencyStatus.Unsupported)]
    [InlineData("..\\..\\surface.def", CharacterDependencyStatus.Ambiguous)]
    [Trait("ValidationTier", "Hermetic")]
    public async Task EscapingOrCrossProviderIncludeCannotBorrowAUniqueBasename(
        string include, CharacterDependencyStatus expected)
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        const string actorName = "data/characters/group/actor.bel";
        string actorText = $"BodyElement(Head, 1, 0, 0, 0, \"root\"); !include(\"{include}\");";
        RetailAssetRecord actor = VirtualFile(actorName, actorText, 2, "declaring-source");
        RetailAssetRecord surface = VirtualFile("data/surface.def", "Setting(7);", 3, "other-source");
        var catalog = new FakeCatalog(root, actor, surface);
        catalog.Payloads.Add(actor.Id.StableKey, Encoding.UTF8.GetBytes(actorText));
        catalog.Payloads.Add(surface.Id.StableKey, Encoding.UTF8.GetBytes("Setting(7);"));

        FbxModelAuthoringImportResult imported = await Dl1CharacterImporter.ImportAsync(mesh, root, catalog,
            new() { CompanionRoots = [actor.Id.LogicalId] });

        CharacterResourceInventory inventory = imported.Package.Document.CharacterResources!;
        Assert.DoesNotContain(inventory.Resources, resource => resource.Id == surface.Id.LogicalId.StableKey);
        Assert.Contains(inventory.Resources, resource => resource.Status == expected &&
            resource.ReferencedBy.Contains(actor.Id.LogicalId.StableKey));
        Assert.Equal(actorText, Encoding.UTF8.GetString(imported.Package.CompanionPayloads[
            inventory.Resources.Single(resource => resource.Id == actor.Id.LogicalId.StableKey).EntryPath!].AsSpan()));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task SameCanonicalPathWithCrossProviderWinnerRemainsUnresolvedInImport()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        const string actorText = "BodyElement(Head, 1, 0, 0, 0, \"root\"); !include(\"../../surface.def\");";
        RetailAssetRecord actor = VirtualFile("data/characters/group/actor.bel", actorText, 2, "declaring-source");
        RetailAssetRecord shadowed = VirtualFile("data/surface.def", "Setting(1);", 3, "declaring-source", priority: 1);
        RetailAssetRecord winner = VirtualFile("data/surface.def", "Setting(2);", 4, "other-source", priority: 10);
        var catalog = new FakeCatalog(root, actor, shadowed, winner);
        catalog.Payloads.Add(actor.Id.StableKey, Encoding.UTF8.GetBytes(actorText));
        catalog.Payloads.Add(shadowed.Id.StableKey, Encoding.UTF8.GetBytes("Setting(1);"));
        catalog.Payloads.Add(winner.Id.StableKey, Encoding.UTF8.GetBytes("Setting(2);"));

        FbxModelAuthoringImportResult imported = await Dl1CharacterImporter.ImportAsync(mesh, root, catalog,
            new() { CompanionRoots = [actor.Id.LogicalId] });

        CharacterResourceInventory inventory = imported.Package.Document.CharacterResources!;
        Assert.DoesNotContain(inventory.Resources, resource => resource.Id == winner.Id.LogicalId.StableKey);
        Assert.Contains(inventory.Resources, resource => resource.Status == CharacterDependencyStatus.Ambiguous &&
            resource.LogicalName == "data/surface.def" && resource.ReferencedBy.Contains(actor.Id.LogicalId.StableKey));
        Assert.Equal(actorText, Encoding.UTF8.GetString(imported.Package.CompanionPayloads[
            inventory.Resources.Single(resource => resource.Id == actor.Id.LogicalId.StableKey).EntryPath!].AsSpan()));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task PackedEffectClosureRetainsOneOriginalBundleAndExactDefinitionReceipts()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        const string actorText = "Effect(\"effects/generic_sequence.fx\");";
        const string sequenceText = "SequenceDef() { ParticleEmiter(\"generic_emitter.fx\", 1); }";
        const string emitterText = "ParticleEmiterDef() { Material(\"effect_material.mat\"); }";
        using var output = new MemoryStream();
        WriteDefinition("effects/generic_sequence", 6, sequenceText);
        WriteDefinition("generic_emitter", 2, emitterText);
        output.WriteByte(0);
        byte[] bundle = output.ToArray();
        var definitions = Rp6lEffectBundleDecoder.Decode(bundle);
        var actor = VirtualFile("generic_actor.bel", actorText, 2, "effects-source");
        var sequence = VirtualFile("effects/generic_sequence.fx", sequenceText, 3, "effects-source");
        sequence = sequence with { Source = sequence.Source with { Kind = RetailAssetSourceKind.RpackEmbeddedEffect } };
        var emitter = VirtualFile("generic_emitter.fx", emitterText, 4, "effects-source");
        emitter = emitter with { Source = emitter.Source with { Kind = RetailAssetSourceKind.RpackEmbeddedEffect } };
        var catalog = new FakeCatalog(root, actor, sequence, emitter);
        catalog.Payloads.Add(actor.Id.StableKey, Encoding.UTF8.GetBytes(actorText));
        var item = new Rp6lItemDescriptor(2, 0, 0, 42, 0, bundle.Length, 0);
        var descriptor = new Rp6lResourceDescriptor(1, "FX", Rp6lResourceTypes.Effect, 0, 2, 1, [item]);
        var chunk = new Rp6lChunkDescriptor(0, 80, 0, 0, bundle.Length, 0, 0, 0, Rp6lCompression.None);
        foreach (var asset in new[] { sequence, emitter })
        {
            var definition = definitions.Single(value => value.Name + ".fx" == asset.Id.Name);
            catalog.Effects.Add(asset.Id.StableKey, new(root, descriptor, item, chunk,
                ImmutableArray.Create(bundle), definition));
        }

        var imported = await Dl1CharacterImporter.ImportAsync(mesh, root, catalog,
            new() { CompanionRoots = [actor.Id.LogicalId] });
        var inventory = imported.Package.Document.CharacterResources!;
        var effectRows = inventory.Resources.Where(resource => resource.PackedEffect is not null).ToArray();
        Assert.Equal(2, effectRows.Length);
        var original = Assert.Single(inventory.Resources, resource => resource.IsOriginalArchive);
        Assert.Equal(bundle, imported.Package.CompanionPayloads[original.EntryPath!].ToArray());
        Assert.All(effectRows, resource => Assert.Equal(original.Id, resource.PackedEffect!.BundleResourceId));
        var nested = Assert.Single(effectRows, resource => resource.LogicalName == "generic_emitter.fx");
        Assert.Contains(sequence.Id.LogicalId.StableKey, nested.ReferencedBy);
        Assert.Equal(2, catalog.CustodyReads);
        Assert.Contains(inventory.Resources, resource => resource.LogicalName == "effect_material.mat" &&
            resource.Subsystem == CharacterSubsystem.Materials && resource.Status == CharacterDependencyStatus.Missing);
        Assert.Equal(2, imported.Surfaces.Length);
        Assert.Equal("smile", Assert.Single(imported.Package.Document.MorphChannels).Name);
        byte[] serialized = CustomModelPackageSerializer.Serialize(imported.Package).ToArray();
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "character.dlrmodel");
            await File.WriteAllBytesAsync(path, serialized);
            var reopened = CustomModelPackageSerializer.Load(path);
            Assert.Equal(effectRows.Select(resource => resource.PackedEffect),
                reopened.Document.CharacterResources!.Resources.Where(resource => resource.PackedEffect is not null)
                    .Select(resource => resource.PackedEffect));
            Assert.Equal(bundle, reopened.CompanionPayloads[original.EntryPath!].ToArray());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }

        void WriteDefinition(string name, byte kind, string text)
        {
            output.Write(Encoding.UTF8.GetBytes(name)); output.WriteByte(0); output.WriteByte(kind);
            output.Write(Encoding.UTF8.GetBytes(text)); output.WriteByte(0);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task VerifiedEffectMaterialNamesRequireOriginalDatabaseCustody()
    {
        (RetailAssetRecord root, Dl1MeshData mesh) = Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => Dl1CharacterImporter.ImportAsync(mesh, root,
            new FakeCatalog(root), new() { VerifiedMaterialNames = ["effect_material.mat"] }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("ValidationTier", "Hermetic")]
    public async Task DeclaredAndParticleMaterialsPreserveExactProviderAndFullTextureItems(bool missingTexture, bool declaredMaterial)
    {
        (RetailAssetRecord root, Dl1MeshData mesh)=Fixture();
        const string source="ParticleDef() { Material(\"effect_material.mat\", \"ignored.mat\"); }";
        if (declaredMaterial)
            mesh = mesh with
            {
                OriginalMaterialDatabase = new(1, 1, [new(0, "effect_material.mat", 0)]),
                MaterialSlots = [new(0, "effect_material.mat", 0, "effect_material.mat", Dl1MaterialBindingStatus.DatabaseNameDecoded)],
            };
        var effect=VirtualFile("particle.fx",source,2,"generic-provider");
        byte[] textureBytes=[1,2,3,4,5,6,7];
        var logical=RetailAssetLogicalId.Rpack(Rp6lResourceTypes.Texture,"generic_texture");
        var texture=new RetailAssetRecord(RetailAssetId.Create(logical,"synthetic","generic-provider",3,1,Sha("texture-source"),null),"generic_texture",
            new("generic-provider",RetailAssetSourceKind.Rpack,1,"generic.rpack","generic_texture",3,textureBytes.Length,textureBytes.Length,DateTime.UnixEpoch));
        var catalog=new FakeCatalog(root, [effect, ..(missingTexture?Array.Empty<RetailAssetRecord>():[texture])]);
        catalog.Payloads.Add(effect.Id.StableKey,Encoding.UTF8.GetBytes(source));
        var items=ImmutableArray.Create(new Rp6lItemDescriptor(4,0,0,3,0,3,0),new Rp6lItemDescriptor(5,1,0,3,0,4,0));
        var resource=new Rp6lResourceDescriptor(3,"generic_texture",Rp6lResourceTypes.Texture,0,4,2,items);
        catalog.Native.Add(texture.Id.StableKey,new(texture,new(1,0,2,2,1,16,1,1),resource,
            [new(items[0],ImmutableArray.Create(textureBytes[..3]),Hash(textureBytes[..3])),new(items[1],ImmutableArray.Create(textureBytes[3..]),Hash(textureBytes[3..]))],
            [new(new(0,32,514,0,3,0,1,2,Rp6lCompression.None),new string('a',64)),new(new(1,33,514,0,4,0,1,2,Rp6lCompression.None),new string('b',64))],Hash(textureBytes)));
        byte[] provider=new byte[116];BinaryPrimitives.WriteUInt32LittleEndian(provider,0x4d444241);BinaryPrimitives.WriteInt32LittleEndian(provider.AsSpan(4),1);BinaryPrimitives.WriteInt32LittleEndian(provider.AsSpan(8),16);
        "materials"u8.CopyTo(provider.AsSpan(16));BinaryPrimitives.WriteInt32LittleEndian(provider.AsSpan(48),1);BinaryPrimitives.WriteInt32LittleEndian(provider.AsSpan(52),1);BinaryPrimitives.WriteInt32LittleEndian(provider.AsSpan(56),64);
        uint nameHash=Dl1ResourceNameHash.Compute("effect_material.mat");BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(64),nameHash);BinaryPrimitives.WriteInt32LittleEndian(provider.AsSpan(68),80);BinaryPrimitives.WriteInt32LittleEndian(provider.AsSpan(72),36);BinaryPrimitives.WriteInt32LittleEndian(provider.AsSpan(76),36);
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(80),nameHash);BinaryPrimitives.WriteUInt16LittleEndian(provider.AsSpan(96),1);BinaryPrimitives.WriteUInt16LittleEndian(provider.AsSpan(98),1);BinaryPrimitives.WriteUInt16LittleEndian(provider.AsSpan(102),2);BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(104),5);BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(108),Dl1ResourceNameHash.ComputeTextureResource("generic_texture"));BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(112),7);
        var imported=await Dl1CharacterImporter.ImportAsync(mesh,root,catalog,new(){CompanionRoots=declaredMaterial?[]:[effect.Id.LogicalId],MaterialProviderResourceId="material-provider",
            ExternalResources=[new("material-provider","generic.mp","generic-materials",ImmutableArray.Create(provider),CharacterSubsystem.Materials)]});
        var inventory=imported.Package.Document.CharacterResources!;
        var material=Assert.Single(inventory.Resources,row=>row.Material is not null);
        Assert.Equal(provider.AsSpan(80,36).ToArray(),imported.Package.CompanionPayloads[material.EntryPath!].ToArray());
        Assert.DoesNotContain(inventory.Resources,row=>row.LogicalName=="ignored.mat");
        if (declaredMaterial)
        {
            Assert.Contains(root.Id.LogicalId.StableKey, material.ReferencedBy);
            Assert.DoesNotContain(inventory.Resources, row => row.LogicalName == "particle.fx");
        }
        if(missingTexture)
        {
            Assert.Null(Assert.Single(material.Material!.Textures).ResourceId);
            Assert.Contains(inventory.ExportBlockers,text=>text.Contains("unresolved texture",StringComparison.Ordinal));
        }
        else
        {
            var retained=Assert.Single(inventory.Resources,row=>row.Id == texture.Id.LogicalId.StableKey);
            Assert.NotNull(retained.NativeResource);
            Assert.Equal(2,retained.NativeResource!.Items.Length);
            Assert.Equal(textureBytes,imported.Package.CompanionPayloads[retained.EntryPath!].ToArray());
            Assert.Equal(retained.Id,Assert.Single(material.Material!.Textures).ResourceId);
        }
        CustomModelPackageSerializer.Serialize(imported.Package);
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task DetachedCompiledMeshRetainsEveryItemAndReportsItsMaterialDependencies()
    {
        (RetailAssetRecord root,Dl1MeshData mesh)=Fixture();
        var fixture=RpackTestData.BuildCompiledMeshFixture();byte[][] payloads=[fixture.Metadata,fixture.Variants,[1,2,3],fixture.Vertices,fixture.Indices];
        int length=payloads.Sum(bytes=>bytes.Length);var logical=RetailAssetLogicalId.Rpack(Rp6lResourceTypes.Mesh,"generic_part");
        var asset=new RetailAssetRecord(RetailAssetId.Create(logical,"synthetic","generic-provider",2,1,Sha("part-source"),null),"generic_part",
            new("generic-provider",RetailAssetSourceKind.Rpack,1,"generic.rpack","generic_part",2,length,length,DateTime.UnixEpoch));
        var catalog=new FakeCatalog(root,asset);int offset=0;
        var items=payloads.Select((bytes,index)=>{var item=new Rp6lItemDescriptor(index,0,0,2,offset,bytes.Length,0);offset+=bytes.Length;return item;}).ToImmutableArray();
        var resource=new Rp6lResourceDescriptor(2,"generic_part",Rp6lResourceTypes.Mesh,0,0,items.Length,items);
        catalog.Native.Add(asset.Id.StableKey,new(asset,new(1,0,items.Length,1,1,13,1,1),resource,
            items.Select((item,index)=>new RetailRpackItemCustody(item,ImmutableArray.Create(payloads[index]),Hash(payloads[index]))).ToImmutableArray(),
            [new(new(0,16,514,0,length,0,1,2,Rp6lCompression.None),new string('a',64))],Hash(payloads.SelectMany(bytes=>bytes).ToArray())));
        var imported=await Dl1CharacterImporter.ImportAsync(mesh,root,catalog,new(){CompanionRoots=[asset.Id.LogicalId]});
        var part=Assert.Single(imported.Package.Document.CharacterResources!.Resources,row=>row.Id == asset.Id.LogicalId.StableKey);
        Assert.Equal(Rp6lResourceTypes.Mesh, part.NativeResource!.ResourceType);
        Assert.Equal(CharacterSubsystem.DetachedParts,part.Subsystem);Assert.Equal(5,part.NativeResource!.Items.Length);
        Assert.Equal(payloads.SelectMany(bytes=>bytes).ToArray(),imported.Package.CompanionPayloads[part.EntryPath!].ToArray());
        Assert.Contains(imported.Package.Document.CharacterResources.Resources,row=>row.Subsystem==CharacterSubsystem.Materials && row.ReferencedBy.Contains(part.Id));
        CustomModelPackageSerializer.Serialize(imported.Package);
    }

    [Fact]
    public async Task IndexedBodyDependenciesUseConcreteNamesAndPreserveTheOriginalTemplateBytes()
    {
        var (root, mesh) = Fixture();
        const string source = "DestroyedHeadParts(\"piece_XX.msh\", 4) // retained\n";
        var body = VirtualFile("data/parts/body.bel", source, 11, "generic-provider");
        var parts = Enumerable.Range(0, 4).Select(index => VirtualFile(
            $"data/parts/piece_{index:D2}.msh", "opaque", 12 + index, "generic-provider")).ToArray();
        var catalog = new FakeCatalog(root, [body, .. parts]);
        catalog.Payloads.Add(body.Id.StableKey, Encoding.UTF8.GetBytes(source));
        var imported = await Dl1CharacterImporter.ImportAsync(mesh, root, catalog, new() { CompanionRoots = [body.Id.LogicalId] });
        var inventory = imported.Package.Document.CharacterResources!;
        var retainedBody = Assert.Single(inventory.Resources, row => row.LogicalName == body.Id.Name);
        Assert.Equal(Encoding.UTF8.GetBytes(source), imported.Package.CompanionPayloads[retainedBody.EntryPath!].ToArray());
        foreach (var part in parts)
        {
            var retained = Assert.Single(inventory.Resources, row => row.LogicalName == part.Id.Name);
            Assert.Equal(CharacterSubsystem.DetachedParts, retained.Subsystem);
            Assert.Equal(CharacterDependencyStatus.Preserved, retained.Status);
            Assert.Contains(retainedBody.Id, retained.ReferencedBy);
        }
        Assert.DoesNotContain(inventory.Resources, row => row.LogicalName.Contains("_XX", StringComparison.Ordinal));
        CustomModelPackageSerializer.Serialize(imported.Package);
    }

    [Fact]
    public async Task GenericPreloadRetainsNineTypedMeshesAndRealDownstreamFindings()
    {
        var (root, mesh) = Fixture();
        var source = new StringBuilder("!include(\"symbols.def\"); ForceGenericRelics(); BodyElement(_HEAD,1,0,0,0,\"root\");\n");
        for (int index = 0; index < 9; index++)
            source.Append(System.Globalization.CultureInfo.InvariantCulture, $"AddRelics(\"Part{index}\",PHYSICS_SINGLE,\"part.phx\",\"\",[0,0,0],[0,0,0]);\n");
        source.Append("BodyElement(_SPINE,1,0,0,0,\"root\"); AddRelics(\"Excluded\",PHYSICS_SINGLE,\"part.phx\",\"\",[0,0,0],[0,0,0]);");
        var body = VirtualFile("data/body/body.bel", source.ToString(), 20, "generic-provider");
        var symbols = VirtualFile("data/body/symbols.def", "$_HEAD(i,0); $_SPINE(i,7);", 21, "generic-provider");
        var physics = VirtualFile("data/body/part.phx", "PhysicsParams(){QuickStepNumIterations(12);}", 22, "generic-provider");
        var assets = Enumerable.Range(0,9).Select(index =>
        {
            string name = "part" + index;
            var logical = RetailAssetLogicalId.Rpack(Rp6lResourceTypes.Mesh,name);
            return new RetailAssetRecord(RetailAssetId.Create(logical,"synthetic","generic-provider",30+index,1,Sha("source-"+index),null),name,
                new("generic-provider",RetailAssetSourceKind.Rpack,1,"generic.rpack",name,30+index,0,0,DateTime.UnixEpoch));
        }).ToArray();
        var catalog = new FakeCatalog(root, [body,symbols,physics,..assets]);
        catalog.Payloads.Add(body.Id.StableKey,Encoding.UTF8.GetBytes(source.ToString()));
        catalog.Payloads.Add(symbols.Id.StableKey,Encoding.UTF8.GetBytes("$_HEAD(i,0); $_SPINE(i,7);"));
        catalog.Payloads.Add(physics.Id.StableKey,Encoding.UTF8.GetBytes("PhysicsParams(){QuickStepNumIterations(12);}"));
        foreach(var asset in assets)
        {
            var fixture=RpackTestData.BuildCompiledMeshFixture();
            byte[][] payloads=[fixture.Metadata,fixture.Variants,[1,2,3],fixture.Vertices,fixture.Indices];
            int offset=0;var items=payloads.Select((bytes,index)=>{var item=new Rp6lItemDescriptor(index,0,0,2,offset,bytes.Length,0);offset+=bytes.Length;return item;}).ToImmutableArray();
            var resource=new Rp6lResourceDescriptor(30+Array.IndexOf(assets,asset),asset.Id.Name,Rp6lResourceTypes.Mesh,0,0,items.Length,items);
            catalog.Native.Add(asset.Id.StableKey,new(asset,new(1,0,items.Length,1,1,13,1,1),resource,
                items.Select((item,index)=>new RetailRpackItemCustody(item,ImmutableArray.Create(payloads[index]),Hash(payloads[index]))).ToImmutableArray(),
                [new(new(0,16,514,0,offset,0,1,2,Rp6lCompression.None),new string('a',64))],Hash(payloads.SelectMany(bytes=>bytes).ToArray())));
        }
        var imported=await Dl1CharacterImporter.ImportAsync(mesh,root,catalog,new(){CompanionRoots=[body.Id.LogicalId]});
        var inventory=imported.Package.Document.CharacterResources!;
        var parts=inventory.Resources.Where(record=>record.NativeResource is {ResourceType:272} && record.Subsystem==CharacterSubsystem.DetachedParts).ToArray();
        Assert.Equal(9,parts.Length);
        Assert.All(parts,record=>{Assert.True(record.Required);Assert.Equal(5,record.NativeResource!.Items.Length);Assert.Contains(body.Id.LogicalId.StableKey,record.ReferencedBy);});
        Assert.DoesNotContain(inventory.Resources,record=>record.LogicalName.Equals("excluded",StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inventory.Resources,record=>record.Subsystem==CharacterSubsystem.Materials && parts.Any(part=>record.ReferencedBy.Contains(part.Id)));
        Assert.Equal(Encoding.UTF8.GetBytes(source.ToString()),imported.Package.CompanionPayloads[inventory.Resources.Single(record=>record.Id==body.Id.LogicalId.StableKey).EntryPath!].ToArray());
        CustomModelPackageSerializer.Serialize(imported.Package);
    }

    private static RetailAssetRecord VirtualFile(string name, string source, int index, string provider, int priority = 1)
    {
        RetailAssetLogicalId logical = RetailAssetLogicalId.VirtualFile(name);
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        RetailAssetId id = RetailAssetId.Create(logical, "synthetic", provider, index, priority,
            Sha("source-" + index), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return new RetailAssetRecord(id, name,
            new(provider, RetailAssetSourceKind.ZipPak, priority, "synthetic.pak", name,
                index, bytes.Length, bytes.Length, DateTime.UnixEpoch));
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static (RetailAssetRecord Root, Dl1MeshData Mesh) Fixture()
    {
        RetailAssetLogicalId logical = RetailAssetLogicalId.Rpack(272, "synthetic_character");
        byte[] rootBytes = RootResourceItems().SelectMany(static item => item).ToArray();
        RetailAssetRecord root = new(RetailAssetId.Create(logical, "synthetic", "synthetic", 1, 1,
            Sha("synthetic-root-archive"), Hash(rootBytes)), "synthetic_character",
            new("synthetic", RetailAssetSourceKind.Rpack, 1, "synthetic.rpack", "synthetic_character",
                1, rootBytes.Length, rootBytes.Length, DateTime.UnixEpoch));
        CompactMeshEntity[] entities = [
            new(0, "root", 0, new CompactBounds(0,0,0,1,1,1), -1, CompactMeshEntityType.Bone, 0, 1, CompactMatrix3x4.Identity, CompactMatrix3x4.Identity, 0, 0),
            new(1, "face", 0, new CompactBounds(0,0,0,1,1,1), 0, CompactMeshEntityType.Bone | CompactMeshEntityType.SkinnedMesh, 0, 2, CompactMatrix3x4.Identity, CompactMatrix3x4.Identity, 0, 0)];
        CompactMeshDocument hierarchy = new(2, 1, 0, entities, []);
        RigDefinition rig = Assert.IsType<RigDefinition>(ReAnimated.DL1.Assets.Meshes.Dl1RigDefinitionFactory.TryCreate("synthetic_character", hierarchy));
        Dl1MeshVertex[] vertices = [new(Vector3.Zero, Vector3.UnitY, Vector4.UnitX, Vector2.Zero, Vector2.Zero, Vector4.One, Vector4.UnitX, new(0,0,0,0)), new(new(1,0,0), Vector3.UnitY, Vector4.UnitX, Vector2.UnitX, Vector2.Zero, Vector4.One, Vector4.UnitX, new(0,0,0,0)), new(new(0,1,0), Vector3.UnitY, Vector4.UnitX, Vector2.UnitY, Vector2.Zero, Vector4.One, Vector4.UnitX, new(0,0,0,0))];
        Dl1MeshSurface Surface(int lod) => new($"face-lod{lod}", 1, lod, 0, new(12, []), new(0,0,36,12), new(1,0,6,2), 3, 3, vertices, [0,1,2], [new(0,0,3,0,[1]) { SkinBindingMode = Dl1SkinBindingMode.RigidIndexedPalette }]);
        var binding = new Dl1MorphBinding(1, 0, 3, 8, 0, Dl1MorphDeltaEncoding.PcHalf4, [], [new(0, [new Vector3(0.2f,0,0), new Vector3(0,0.2f,0), new Vector3(0,0,0.2f)])]);
        Dl1MorphTarget morph = new(0, "smile", [1], [], [binding], Dl1MorphPayloadStatus.VertexDeltasDecoded);
        Dl1MeshData mesh = new("synthetic_character", Dl1MeshContainerLayout.FiveItemSplitGpu, hierarchy, rig, [], [Surface(0), Surface(1)], [new(0, "synthetic", null, null, Dl1MaterialBindingStatus.DeclaredSlotNameUnresolved)], [morph], [], []);
        return (root, mesh);
    }

    private static byte[][] RootResourceItems()
    {
        CompiledMeshTestFixture fixture = RpackTestData.BuildCompiledMeshFixture();
        return [fixture.Metadata, fixture.Variants, [1, 2, 3], fixture.Vertices, fixture.Indices];
    }

    private static RetailRpackResourceCustody RootCustody(RetailAssetRecord root)
    {
        byte[][] payloads = RootResourceItems();
        int offset = 0;
        ImmutableArray<Rp6lItemDescriptor> items = payloads.Select((bytes, index) =>
        {
            var item = new Rp6lItemDescriptor(index, 0, 0, 2, offset, bytes.Length, 0);
            offset += bytes.Length;
            return item;
        }).ToImmutableArray();
        string contentHash = Hash(payloads.SelectMany(static bytes => bytes).ToArray());
        return new(root, new(1, 0, items.Length, 1, 1, 13, 1, 1),
            new(1, root.Id.Name, Rp6lResourceTypes.Mesh, 0, 0, items.Length, items),
            items.Select((item, index) => new RetailRpackItemCustody(item,
                ImmutableArray.Create(payloads[index]), Hash(payloads[index]))).ToImmutableArray(),
            [new(new(0, 16, 514, 0, offset, 0, 1, 2, Rp6lCompression.None), contentHash)], contentHash);
    }

    private sealed class FakeCatalog(RetailAssetRecord root, params RetailAssetRecord[] companions) : IRetailAssetCatalog, IRetailEmbeddedEffectCatalog, IRetailRpackResourceCatalog
    {
        public Dictionary<string, byte[]> Payloads { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RetailEmbeddedEffectCustody> Effects { get; } = new(StringComparer.Ordinal);
        public Dictionary<string,RetailRpackResourceCustody> Native {get;} = new(StringComparer.Ordinal)
        {
            [root.Id.StableKey] = RootCustody(root),
        };
        public ValueTask<RetailRpackResourceCustody> ReadRpackResourceCustodyAsync(RetailAssetRecord asset,CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();return new(Native[asset.Id.StableKey]);}
        public int CustodyReads { get; private set; }
        public ValueTask<RetailEmbeddedEffectCustody> ReadEmbeddedCustodyAsync(RetailAssetRecord asset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CustodyReads++;
            return new(Effects[asset.Id.StableKey]);
        }
        public IReadOnlyList<RetailAssetRecord> Assets => [root, .. companions];
        public IReadOnlyList<RetailAssetConflict> Conflicts => [];
        public RetailAssetRecord? Resolve(RetailAssetLogicalId id) => GetCandidates(id)
            .OrderByDescending(asset => asset.Id.Precedence).FirstOrDefault();
        public IReadOnlyList<RetailAssetRecord> GetCandidates(RetailAssetLogicalId id) => Assets
            .Where(asset => asset.Id.LogicalId.StableKey == id.StableKey).ToArray();
        public IReadOnlyList<RetailAssetRecord> Search(string text, int maximumResults = 500) => Assets;
        public ValueTask<Stream> OpenReadAsync(RetailAssetLogicalId id, CancellationToken cancellationToken = default) => new(new MemoryStream([1,2,3]));
        public ValueTask<Stream> OpenReadAsync(RetailAssetId id, CancellationToken cancellationToken = default) => new(new MemoryStream([1,2,3]));
        public ValueTask<Stream> OpenReadAsync(RetailAssetRecord asset, CancellationToken cancellationToken = default) =>
            new(new MemoryStream(Payloads.TryGetValue(asset.Id.StableKey, out byte[]? bytes) ? bytes :
                asset.Id.Namespace == RetailAssetNamespace.VirtualFile ? Encoding.UTF8.GetBytes("Setting(1);") : [1,2,3]));
    }
}

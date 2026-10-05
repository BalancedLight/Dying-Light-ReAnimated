using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Cli;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

[Collection("Character CLI")]
public sealed class CharacterSupplementalSourceAuthoringTests
{
    private const string Member = "sources/effects/generic.fx";
    private const string VirtualName = "data/effects/generic.fx";

    [Fact]
    public async Task AttachmentPreservesUnknownBytesProvenanceMissingReferencesAndGeometryAcrossSave()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] source = Encoding.UTF8.Preamble.ToArray().Concat(Encoding.UTF8.GetBytes(
                "// supplemental source\r\nUnknownEffect(17, \"custom-token\");\r\n")).ToArray();
            string archive = await CreateZipAsync(directory, [(Member, source)]);
            var baseline = CreatePackage();
            var baselineInventory = baseline.Document.CharacterResources!;
            var original = baseline with { Document = baseline.Document with { CharacterResources = baselineInventory with
            {
                Resources = baselineInventory.Resources.Add(baselineInventory.Resources[^1] with { Id = "second-missing-effect" }),
            } } };
            var attached = await CharacterSupplementalSourceAuthoring.AttachAsync(original, archive, Member,
                VirtualName, CharacterSubsystem.Damage, Sha(source), reviewed: true, Sha(await File.ReadAllBytesAsync(archive)));
            var inventory = attached.Document.CharacterResources!;
            var resource = inventory.Resources[^1];
            Assert.Equal(CharacterDependencyStatus.Ambiguous, resource.Status);
            Assert.Equal(2, inventory.Resources.Count(record => record.LogicalName == VirtualName && record.Status == CharacterDependencyStatus.Missing));
            Assert.True(resource.Required);
            Assert.Equal(VirtualName, resource.LogicalName);
            Assert.Equal(source, attached.CompanionPayloads[resource.EntryPath!].ToArray());
            var receipt = Assert.IsType<CharacterSupplementalSourceReceipt>(resource.SupplementalSource);
            Assert.Equal(Member, receipt.MemberName);
            Assert.Equal(VirtualName, receipt.VirtualName);
            Assert.Equal(Sha(source), receipt.ContentSha256);
            Assert.Equal(Sha(await File.ReadAllBytesAsync(archive)), receipt.ArchiveSha256);
            Assert.Equal(new FileInfo(archive).Length, receipt.ArchiveByteLength);
            Assert.Equal(receipt.ArchiveSha256, resource.SourceFingerprint);
            Assert.Equal("zip-sha256:" + receipt.ArchiveSha256, resource.ProviderIdentity);
            Assert.True(receipt.Reviewed);
            Assert.Equal(JsonSerializer.Serialize(original.Document.CharacterResources!.Subsystems),
                JsonSerializer.Serialize(inventory.Subsystems));
            Assert.Equal(JsonSerializer.Serialize(original.Document.CharacterResources.Resources),
                JsonSerializer.Serialize(inventory.Resources.Take(original.Document.CharacterResources.Resources.Length)));
            Assert.Contains(inventory.Resources, record => record.Id == "missing-effect" && record.Status == CharacterDependencyStatus.Missing);
            Assert.All(original.Document.CharacterResources.ExportBlockers, blocker => Assert.Contains(blocker, inventory.ExportBlockers));
            Assert.Contains(inventory.ExportBlockers, blocker => blocker.Contains("Supplemental source", StringComparison.Ordinal));
            Assert.Equal(original.SourceFbx.ToArray(), attached.SourceFbx.ToArray());
            Assert.Equal(original.DecodedCharacterPayload.ToArray(), attached.DecodedCharacterPayload.ToArray());
            Assert.Equal(original.GeometryRevisionPayload.ToArray(), attached.GeometryRevisionPayload.ToArray());
            Assert.Equal(JsonSerializer.Serialize(original.Document.Bones), JsonSerializer.Serialize(attached.Document.Bones));
            Assert.Equal(JsonSerializer.Serialize(original.Document.MorphAuthoringRecords), JsonSerializer.Serialize(attached.Document.MorphAuthoringRecords));
            Assert.Null(inventory.CompiledSemanticSha256);
            Assert.Null(inventory.LoadedResourceSha256);
            Assert.Empty(inventory.VerifiedPlayerScenarios);
            string output = Path.Combine(directory, "attached.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(attached, output);
            var reopened = CustomModelPackageSerializer.Load(output);
            Assert.Equal(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(reopened.Document.CharacterResources!.Resources[^1].SupplementalSource));
            Assert.Equal(source, reopened.CompanionPayloads[resource.EntryPath!].ToArray());
            Assert.All(original.Document.CharacterResources.ExportBlockers,
                blocker => Assert.Contains(blocker, reopened.Document.CharacterResources.ExportBlockers));
            Assert.DoesNotContain(directory, JsonSerializer.Serialize(receipt), StringComparison.OrdinalIgnoreCase);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task UnknownBinaryMemberIsRetainedAsPendingAssociation()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] bytes = [0xff, 0, 0x81, 0x72];
            string archive = await CreateZipAsync(directory, [("assets/part.msh", bytes)]);
            var attached = await CharacterSupplementalSourceAuthoring.AttachAsync(CreatePackage(), archive,
                "assets/part.msh", "data/parts/part.msh", CharacterSubsystem.DetachedParts, Sha(bytes), true);
            var resource = attached.Document.CharacterResources!.Resources[^1];
            Assert.Equal(CharacterDependencyStatus.Ambiguous, resource.Status);
            Assert.Equal(bytes, attached.CompanionPayloads[resource.EntryPath!].ToArray());
            Assert.True(resource.Required);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData("unreviewed")]
    [InlineData("content-hash")]
    [InlineData("archive-hash")]
    [InlineData("missing-member")]
    [InlineData("member-case")]
    [InlineData("unsafe-member")]
    [InlineData("unsafe-virtual")]
    [InlineData("extension-mismatch")]
    [InlineData("duplicate-entry")]
    [InlineData("case-entry")]
    [InlineData("unsafe-entry")]
    [InlineData("payload-collision")]
    [InlineData("root-collision")]
    [InlineData("virtual-case")]
    [InlineData("provider-ambiguity")]
    [InlineData("bad-utf8")]
    [InlineData("nul-script")]
    [InlineData("oversize-member")]
    [InlineData("truncated-member")]
    [InlineData("directory-bounds")]
    [InlineData("directory-count")]
    [InlineData("zip64-count")]
    public async Task UnsafeStaleOrAmbiguousSelectionsDoNotChangeThePackage(string scenario)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes("UnknownEffect(42);");
            var package = CreatePackage();
            string member = Member, virtualName = VirtualName;
            string expected = Sha(bytes);
            string? archiveExpected = null;
            bool reviewed = true;
            List<(string Name, byte[] Bytes)> entries = [(Member, bytes)];
            switch (scenario)
            {
                case "unreviewed": reviewed = false; break;
                case "content-hash": expected = new string('0', 64); break;
                case "archive-hash": archiveExpected = new string('0', 64); break;
                case "missing-member": member = "sources/effects/absent.fx"; break;
                case "member-case": member = Member.ToUpperInvariant(); break;
                case "unsafe-member": member = "../effect.fx"; break;
                case "unsafe-virtual": virtualName = "data/../effect.fx"; break;
                case "extension-mismatch": virtualName = "data/effects/generic.mat"; break;
                case "duplicate-entry": entries.Add((Member, bytes)); break;
                case "case-entry": entries.Add((Member.ToUpperInvariant(), bytes)); break;
                case "unsafe-entry": entries.Add(("../unrelated.bin", [1])); break;
                case "payload-collision": break;
                case "root-collision":
                    virtualName = package.Document.CharacterResources!.Resources[0].LogicalName;
                    member = "sources/root" + Path.GetExtension(virtualName);
                    entries = [(member, bytes)];
                    break;
                case "virtual-case": virtualName = VirtualName.ToUpperInvariant(); break;
                case "provider-ambiguity":
                {
                    var inventory = package.Document.CharacterResources!;
                    package = package with { Document = package.Document with { CharacterResources =
                        inventory with { Resources = inventory.Resources.Add(inventory.Resources[^1] with { Id = "another-provider", Status = CharacterDependencyStatus.Ambiguous }) } } };
                    break;
                }
                case "bad-utf8": bytes = [0xff, 0xfe]; entries = [(Member, bytes)]; expected = Sha(bytes); break;
                case "nul-script": bytes = [65, 0, 66]; entries = [(Member, bytes)]; expected = Sha(bytes); break;
                case "oversize-member":
                case "truncated-member":
                case "directory-bounds":
                case "directory-count":
                case "zip64-count": break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            // A payload collision must retain a valid, separate root resource.
            if (scenario == "payload-collision")
            {
                var baseline = CreatePackage();
                var inventory = baseline.Document.CharacterResources!;
                var root = inventory.Resources[0];
                package = baseline with { Document = baseline.Document with { CharacterResources =
                    inventory with { Resources = inventory.Resources.Add(
                        root with { Id = "existing-source", LogicalName = VirtualName, EntryPath = "character/resources/existing.bin" }) } },
                    CompanionPayloads = baseline.CompanionPayloads.Add("character/resources/existing.bin",
                        baseline.CompanionPayloads[root.EntryPath!]) };
                virtualName = VirtualName;
            }
            string archive = await CreateZipAsync(directory, entries);
            if (scenario is "oversize-member" or "truncated-member")
            {
                byte[] zip = await File.ReadAllBytesAsync(archive);
                int central = FindSignature(zip, 0x02014b50);
                Assert.True(central >= 0);
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(central + 24, 4),
                    scenario == "oversize-member" ? (uint)CharacterSupplementalSourceReceipt.MaximumMemberBytes + 1 : (uint)bytes.Length + 1);
                await File.WriteAllBytesAsync(archive, zip);
            }
            if (scenario == "directory-bounds")
            {
                byte[] zip = await File.ReadAllBytesAsync(archive);
                int end = FindSignature(zip, 0x06054b50);
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(end + 12, 4),
                    (uint)CharacterSupplementalSourceAuthoring.MaximumDirectoryBytes + 1);
                await File.WriteAllBytesAsync(archive, zip);
            }
            if (scenario == "directory-count")
            {
                byte[] zip = await File.ReadAllBytesAsync(archive);
                int end = FindSignature(zip, 0x06054b50);
                BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(end + 8, 2), 2);
                BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(end + 10, 2), 2);
                await File.WriteAllBytesAsync(archive, zip);
            }
            if (scenario == "zip64-count")
                await File.WriteAllBytesAsync(archive, AsZip64(await File.ReadAllBytesAsync(archive),
                    (ulong)CharacterSupplementalSourceAuthoring.MaximumArchiveEntries + 1));
            byte[] before = CustomModelPackageSerializer.Serialize(package).ToArray();
            byte[] archiveBefore = await File.ReadAllBytesAsync(archive);

            var error = await Record.ExceptionAsync(() => CharacterSupplementalSourceAuthoring.AttachAsync(package,
                archive, member, virtualName, CharacterSubsystem.Damage, expected, reviewed, archiveExpected));

            Assert.NotNull(error);
            Assert.True(error is ArgumentException or InvalidOperationException or InvalidDataException or IOException);
            Assert.Equal(before, CustomModelPackageSerializer.Serialize(package).ToArray());
            Assert.Equal(archiveBefore, await File.ReadAllBytesAsync(archive));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task ReceiptTamperingFailsPackageValidation()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes("UnknownEffect(9);");
            string archive = await CreateZipAsync(directory, [(Member, bytes)]);
            var attached = await CharacterSupplementalSourceAuthoring.AttachAsync(CreatePackage(), archive, Member,
                VirtualName, CharacterSubsystem.Damage, Sha(bytes), true);
            var inventory = attached.Document.CharacterResources!;
            var resource = inventory.Resources[^1];
            var changed = attached with { Document = attached.Document with { CharacterResources = inventory with
            {
                Resources = inventory.Resources.SetItem(inventory.Resources.Length - 1, resource with
                { SupplementalSource = resource.SupplementalSource! with { ContentSha256 = new string('0', 64) } }),
            } } };
            Assert.Throws<ArgumentException>(() => CustomModelPackageSerializer.Serialize(changed));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task CliAttachmentRequiresReviewAndNewOutputAndPersistsPendingSource()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes("UnknownEffect(12);");
            string archive = await CreateZipAsync(directory, [(Member, bytes)]);
            string source = Path.Combine(directory, "character.dlrmodel");
            string output = Path.Combine(directory, "attached.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(CreatePackage(), source);
            byte[] sourceBefore = await File.ReadAllBytesAsync(source);
            string[] args = ["character", "attach-source", source, "--archive", archive, "--member", Member,
                "--virtual-name", VirtualName, "--subsystem", "damage", "--expected-sha256", Sha(bytes),
                "--archive-sha256", Sha(await File.ReadAllBytesAsync(archive)), "--reviewed", "--output", output];
            Assert.Equal(2, await CliApplication.RunAsync(args.Where(value => value != "--reviewed").ToArray()));
            Assert.False(File.Exists(output));
            Assert.Equal(0, await CliApplication.RunAsync(args));
            var attached = CustomModelPackageSerializer.Load(output);
            var resource = attached.Document.CharacterResources!.Resources[^1];
            Assert.Equal(CharacterDependencyStatus.Ambiguous, resource.Status);
            Assert.Equal(bytes, attached.CompanionPayloads[resource.EntryPath!].ToArray());
            Assert.Equal(Member, resource.SupplementalSource!.MemberName);
            Assert.Contains(attached.Document.CharacterResources.Resources, record => record.Id == "missing-effect");
            byte[] outputBefore = await File.ReadAllBytesAsync(output);
            Assert.Equal(2, await CliApplication.RunAsync(args));
            Assert.Equal(outputBefore, await File.ReadAllBytesAsync(output));
            Assert.Equal(2, await CliApplication.RunAsync(args.Concat(["--member", Member]).ToArray()));
            Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(source));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task Zip64MemberUsesExactSourceBytesAndCancellationStopsBeforeAttachment()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] source = Encoding.UTF8.GetBytes("UnknownEffect(8);");
            string archive = await CreateZipAsync(directory, [(Member, source)]);
            await File.WriteAllBytesAsync(archive, AsZip64(await File.ReadAllBytesAsync(archive), 1));
            var package = CreatePackage();
            var attached = await CharacterSupplementalSourceAuthoring.AttachAsync(package, archive, Member,
                VirtualName, CharacterSubsystem.Damage, Sha(source), true);
            var resource = attached.Document.CharacterResources!.Resources[^1];
            Assert.Equal(source, attached.CompanionPayloads[resource.EntryPath!].ToArray());
            Assert.Equal(Sha(await File.ReadAllBytesAsync(archive)), resource.SupplementalSource!.ArchiveSha256);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CharacterSupplementalSourceAuthoring.AttachAsync(package, archive, Member, VirtualName,
                    CharacterSubsystem.Damage, Sha(source), true, cancellationToken: cancellation.Token));
            Assert.DoesNotContain(package.Document.CharacterResources!.Resources, record => record.SupplementalSource is not null);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData("numeric")]
    [InlineData("rename")]
    public async Task CompanionChangesArchiveSupplementalReceiptAndPersistAuthoredDerivative(string operation)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes("PhysicsParams() { QuickStepNumIterations(12) }\n");
            string archive = await CreateZipAsync(directory, [("sources/physics/generic.phx", bytes)]);
            var attached = await CharacterSupplementalSourceAuthoring.AttachAsync(CreatePackage(), archive,
                "sources/physics/generic.phx", "data/physics/generic.phx", CharacterSubsystem.Ragdoll, Sha(bytes), true);
            var original = attached.Document.CharacterResources!.Resources[^1];
            var read = CharacterCompanionAuthoring.Read(attached, original.Id, CharacterCompanionFamily.Ragdoll);
            Assert.True(read.IsValid);
            var call = Assert.Single(read.Syntax.Calls, row => row.Name == "QuickStepNumIterations");
            int callIndex = read.Syntax.Calls.IndexOf(call);
            byte[] before = CustomModelPackageSerializer.Serialize(attached).ToArray();
            CharacterCompanionAuthoringResult stale = operation == "numeric"
                ? CharacterCompanionAuthoring.ApplyNumericEdit(attached, new(original.Id,
                    CharacterCompanionFamily.Ragdoll, new string('0', 64), callIndex, 0, "12", 18))
                : CharacterCompanionAuthoring.ApplyResourceRename(attached, new(original.Id, new string('0', 64), "renamed.phx"));
            Assert.False(stale.Applied);
            Assert.Same(attached, stale.Package);
            Assert.Equal(before, CustomModelPackageSerializer.Serialize(attached).ToArray());

            CharacterCompanionAuthoringResult changed = operation == "numeric"
                ? CharacterCompanionAuthoring.ApplyNumericEdit(attached, new(original.Id,
                    CharacterCompanionFamily.Ragdoll, original.ContentSha256!, callIndex, 0, "12", 18))
                : CharacterCompanionAuthoring.ApplyResourceRename(attached, new(original.Id, original.ContentSha256!, "renamed.phx"));

            Assert.True(changed.Applied, string.Join("; ", changed.Diagnostics.Select(diagnostic => diagnostic.Message)));
            var active = Assert.Single(changed.Package.Document.CharacterResources!.Resources, row => row.Id == original.Id);
            var archived = Assert.Single(changed.Package.Document.CharacterResources.Resources,
                row => row.Id == "original:" + original.Id);
            Assert.Null(active.SupplementalSource);
            Assert.True(archived.IsOriginalArchive);
            Assert.Equal(JsonSerializer.Serialize(original.SupplementalSource), JsonSerializer.Serialize(archived.SupplementalSource));
            Assert.Equal(original.LogicalName, archived.LogicalName);
            Assert.Equal(original.ContentSha256, archived.ContentSha256);
            Assert.Equal(bytes, changed.Package.CompanionPayloads[archived.EntryPath!].ToArray());
            if (operation == "numeric")
                Assert.Contains("QuickStepNumIterations(18)", Encoding.UTF8.GetString(changed.Package.CompanionPayloads[active.EntryPath!].AsSpan()), StringComparison.Ordinal);
            else
            {
                Assert.Equal("data/physics/renamed.phx", active.LogicalName);
                Assert.Equal(bytes, changed.Package.CompanionPayloads[active.EntryPath!].ToArray());
            }
            string output = Path.Combine(directory, "edited.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(changed.Package, output);
            var reopened = CustomModelPackageSerializer.Load(output);
            var reopenedArchive = Assert.Single(reopened.Document.CharacterResources!.Resources,
                row => row.Id == archived.Id);
            Assert.Equal(JsonSerializer.Serialize(original.SupplementalSource), JsonSerializer.Serialize(reopenedArchive.SupplementalSource));
            Assert.Equal(bytes, reopened.CompanionPayloads[reopenedArchive.EntryPath!].ToArray());
            Assert.Equal(attached.SourceFbx.ToArray(), reopened.SourceFbx.ToArray());
            Assert.Equal(attached.DecodedCharacterPayload.ToArray(), reopened.DecodedCharacterPayload.ToArray());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task ReferenceAdoptionPreservesOriginalSupplementalArchiveReceipt()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes("UnknownEffect(25);");
            string archive = await CreateZipAsync(directory, [(Member, bytes)]);
            var attached = await CharacterSupplementalSourceAuthoring.AttachAsync(CreatePackage(), archive, Member,
                VirtualName, CharacterSubsystem.Damage, Sha(bytes), true);
            var receipt = attached.Document.CharacterResources!.Resources[^1].SupplementalSource;
            var target = FbxModelAuthoringImporter.Import(
                ModelsWorkspaceMorphAuthoringTests.CreateGenericManualSculptFbx(0), "neutral.fbx",
                new() { DecodeAnimationClips = false });
            var reference = FbxModelAuthoringImporter.ImportPackage(attached);
            Assert.Equal(Sha(target.Package.SourceFbx.ToArray()), target.Package.Document.Source.ContentSha256);
            Assert.Equal(Sha(reference.Package.SourceFbx.ToArray()), reference.Package.Document.Source.ContentSha256);
            var adopted = CharacterReferenceAuthoring.AdoptReference(target, reference);
            var archived = Assert.Single(adopted.Package.Document.CharacterResources!.Resources,
                row => row.SupplementalSource is not null);
            Assert.True(archived.IsOriginalArchive);
            Assert.Equal(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(archived.SupplementalSource));
            Assert.Equal(bytes, adopted.Package.CompanionPayloads[archived.EntryPath!].ToArray());
            string output = Path.Combine(directory, "adopted.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(adopted.Package, output);
            var reopened = CustomModelPackageSerializer.Load(output);
            var persisted = Assert.Single(reopened.Document.CharacterResources!.Resources,
                row => row.SupplementalSource is not null);
            Assert.Equal(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(persisted.SupplementalSource));
            Assert.Equal(bytes, reopened.CompanionPayloads[persisted.EntryPath!].ToArray());
            Assert.Equal(target.Package.SourceFbx.ToArray(), reopened.SourceFbx.ToArray());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static byte[] AsZip64(byte[] zip, ulong entries)
    {
        int end = FindSignature(zip, 0x06054b50);
        Assert.True(end >= 0);
        uint directorySize = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 12, 4));
        uint directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16, 4));
        byte[] result = new byte[end + 98];
        zip.AsSpan(0, end).CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end, 4), 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(end + 4, 8), 44);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(end + 12, 2), 45);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(end + 14, 2), 45);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(end + 24, 8), entries);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(end + 32, 8), entries);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(end + 40, 8), directorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(end + 48, 8), directoryOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 56, 4), 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(end + 64, 8), (ulong)end);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 72, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 76, 4), 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(end + 84, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(end + 86, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 88, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 92, 4), uint.MaxValue);
        return result;
    }

    private static CustomModelPackage CreatePackage()
    {
        var package = ModelsWorkspaceMorphAuthoringTests.CreateGenericReferencePackage();
        var inventory = package.Document.CharacterResources!;
        return package with { Document = package.Document with { CharacterResources = inventory with
        {
            Resources = inventory.Resources.Add(new()
            {
                Id = "missing-effect", LogicalName = VirtualName, ProviderIdentity = "catalog",
                SourceFingerprint = new string('a', 64), Subsystem = CharacterSubsystem.Damage,
                Status = CharacterDependencyStatus.Missing, Detail = "Declared effect is missing.",
                ReferencedBy = [inventory.RootResourceId],
            }),
            CompiledSemanticSha256 = new string('b', 64), LoadedResourceSha256 = new string('b', 64),
            VerifiedPlayerScenarios = ["stock-reuse", "facial", "ragdoll", "gore"],
        } } };
    }

    private static async Task<string> CreateZipAsync(string directory, IEnumerable<(string Name, byte[] Bytes)> entries)
    {
        string path = Path.Combine(directory, "sources.zip");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            foreach (var (name, bytes) in entries)
            {
                var entry = archive.CreateEntry(name);
                await using var stream = entry.Open();
                await stream.WriteAsync(bytes);
            }
        return path;
    }

    private static int FindSignature(byte[] bytes, uint signature)
    {
        for (int index = 0; index <= bytes.Length - 4; index++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) == signature) return index;
        return -1;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
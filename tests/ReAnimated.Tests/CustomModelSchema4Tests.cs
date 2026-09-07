using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CustomModelSchema4Tests
{
    [Fact]
    public void ModelOwnedSecondaryDefinitionAndFacialPresetsRoundTrip()
    {
        CustomModelPackage package = Package();
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "portable.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(package, path);
            CustomModelPackage read = CustomModelPackageSerializer.Load(path);
            Assert.Equal(CustomModelDocument.CurrentSchemaVersion, read.Document.SchemaVersion);
            Assert.Equal(package.Document.SecondaryMotion.NativeSources.ToArray(), read.Document.SecondaryMotion.NativeSources.ToArray());
            Assert.Equal(SecondaryMotionSetupSerializer.Serialize(package.Document.SecondaryMotion), SecondaryMotionSetupSerializer.Serialize(read.Document.SecondaryMotion));
            Assert.Equal("Bright", Assert.Single(read.Document.FacialPresets.Presets).Name);
            Assert.Equal(package.SourceFbx.ToArray(), read.SourceFbx.ToArray());
            Assert.Equal(CustomModelPackageSerializer.Serialize(package).ToArray(), CustomModelPackageSerializer.Serialize(read).ToArray());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LegacyPackagesMigrateWithEmptyOptionalFeatures(int schema)
    {
        CustomModelPackage package = Package();
        byte[] bytes = CustomModelPackageSerializer.Serialize(package).ToArray();
        using var input = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var outputStream = new MemoryStream();
        using (var output = new ZipArchive(outputStream, ZipArchiveMode.Create, true))
        {
            foreach (ZipArchiveEntry entry in input.Entries)
            {
                using Stream read = entry.Open();
                using Stream write = output.CreateEntry(entry.FullName).Open();
                if (entry.FullName == CustomModelPackage.ManifestEntryPath)
                {
                    JsonObject json = JsonNode.Parse(read)!.AsObject();
                    json["schemaVersion"] = schema;
                    json.Remove("secondaryMotion");
                    json.Remove("facialPresets");
                    JsonSerializer.Serialize(write, json);
                }
                else read.CopyTo(write);
            }
        }
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "legacy.dlrmodel");
            File.WriteAllBytes(path, outputStream.ToArray());
            CustomModelPackage migrated = CustomModelPackageSerializer.Load(path);
            Assert.Equal(CustomModelDocument.CurrentSchemaVersion, migrated.Document.SchemaVersion);
            Assert.Empty(migrated.Document.SecondaryMotion.Groups);
            Assert.Empty(migrated.Document.SecondaryMotion.NativeSources);
            Assert.Empty(migrated.Document.FacialPresets.Presets);
            Assert.Equal(package.SourceFbx.ToArray(), migrated.SourceFbx.ToArray());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public void SetupJsonRejectsUnknownFieldsAndKeepsNativeTextSeparateFromTuning()
    {
        SecondaryMotionDefinition definition = Package().Document.SecondaryMotion;
        string text = SecondaryMotionSetupSerializer.Serialize(definition);
        var vm = new SecondaryMotionViewModel();
        vm.Load(SecondaryMotionSetupSerializer.Deserialize(text));
        vm.Damping = 7;
        Assert.Equal(definition.NativeSources.ToArray(), vm.Definition.NativeSources.ToArray());
        Assert.Equal(7, vm.Definition.Groups[0].Preview.Damping);
        Assert.Throws<JsonException>(() => SecondaryMotionSetupSerializer.Deserialize(text.Replace("\"groups\"", "\"misspelledGroups\"", StringComparison.Ordinal)));
        vm.GroupEnabled = false;
        Assert.False(vm.Definition.Groups[0].Enabled);
    }

    [Fact]
    public void SchemaFourMigrationPreservesPhysicsAndPresetsAndDefaultsToAuthoredBank()
    {
        CustomModelPackage original = Package();
        byte[] bytes = CustomModelPackageSerializer.Serialize(original).ToArray();
        using var source = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var buffer = new MemoryStream();
        using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using Stream input = entry.Open();
                using Stream target = output.CreateEntry(entry.FullName).Open();
                if (entry.FullName == CustomModelPackage.ManifestEntryPath)
                {
                    JsonObject json = JsonNode.Parse(input)!.AsObject();
                    json["schemaVersion"] = 4;
                    json["buildSettings"]!.AsObject().Remove("referenceExistingAnimationLibrary");
                    JsonSerializer.Serialize(target, json);
                }
                else input.CopyTo(target);
            }
        }
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "schema-four.dlrmodel");
            File.WriteAllBytes(path, buffer.ToArray());
            var migrated = CustomModelPackageSerializer.Load(path);
            Assert.Equal(5, migrated.Document.SchemaVersion);
            Assert.False(migrated.Document.BuildSettings.ReferenceExistingAnimationLibrary);
            Assert.Equal(SecondaryMotionSetupSerializer.Serialize(original.Document.SecondaryMotion), SecondaryMotionSetupSerializer.Serialize(migrated.Document.SecondaryMotion));
            Assert.Equal("Bright", Assert.Single(migrated.Document.FacialPresets.Presets).Name);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public void PackageRejectsMissingSecondaryOrFacialTargets()
    {
        CustomModelDocument document = Package().Document;
        Assert.Throws<ArgumentException>(() => (document with { Bones = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (document with { MorphChannels = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (document with
        {
            Bones = document.Bones.SetItem(2, document.Bones[2] with { Kind = ReAnimated.Core.Domain.BoneKind.Camera }),
        }).Validate());
    }

    private static CustomModelPackage Package()
    {
        ImmutableArray<byte> source = [1, 2, 3, 4, 5];
        string hash = Convert.ToHexStringLower(SHA256.HashData(source.AsSpan()));
        SecondaryMotionDefinition motion = SecondaryMotionTests.Definition() with
        {
            NativeSources = [new() { Kind = NativeClothSourceKind.Phx, ResourceName = "cloth.phx", Text = "FutureStatement(7)\n" }],
        };
        string[] names = ["root", "secondary_anchor", "secondary_tip", "weapon", "camera"];
        var document = new CustomModelDocument
        {
            Source = new() { OriginalFileName = "synthetic.fbx", ContentSha256 = hash, FbxVersion = 7400 },
            RigSignature = hash,
            MorphSignature = hash,
            Bones = names.Select((name, index) => new CustomModelBone { Name = name, Index = index, FbxObjectId = index + 1 }).ToImmutableArray(),
            MorphChannels = [new() { Name = "eye_wide", Index = 0, BlendShapeChannelObjectId = 100, ShapeObjectId = 101, GeometryObjectIds = [102] }],
            SecondaryMotion = motion,
            FacialPresets = new() { Presets = [new() { Name = "Bright", Weights = ImmutableDictionary<string, double>.Empty.Add("eye_wide", 0.7) }] },
        };
        return new(document, source, ImmutableDictionary<string, ImmutableArray<byte>>.Empty);
    }
}

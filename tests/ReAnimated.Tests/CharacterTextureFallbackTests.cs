using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Materials;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Materials;

namespace ReAnimated.Tests;

public sealed class CharacterTextureFallbackTests
{
    [Fact]
    public void ExactReviewedMissingTextureSurvivesSaveReloadAndPreservesNativeReference()
    {
        var (package, receipt) = Create();
        Assert.Contains(package.Document.CharacterResources!.ExportBlockers, b => b.Contains("unresolved texture", StringComparison.Ordinal));
        var reviewed = CharacterTextureFallbackAuthoring.Review(package, receipt);
        Assert.DoesNotContain(reviewed.Document.CharacterResources!.ExportBlockers, b => b.Contains("unresolved texture", StringComparison.Ordinal));
        Assert.True(reviewed.Document.CharacterResources.HasApplicableTextureFallback(reviewed.Document.CharacterResources.Resources.Single(r => r.Id == "missing-texture")));
        string directory = RpackTestData.CreateTemporaryDirectory();
        CustomModelPackage reopened;
        try
        {
            string path = Path.Combine(directory, "character.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(reviewed, path);
            reopened = CustomModelPackageSerializer.Load(path);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
        CharacterTextureFallbackAuthoring.Revalidate(reopened);
        Assert.False(reopened.Document.CharacterResources!.IsGameReady);
        Assert.Equal(package.CompanionPayloads["character/resources/material.bin"].ToArray(), reopened.CompanionPayloads["character/resources/material.bin"].ToArray());
        Assert.Null(reopened.Document.CharacterResources.Resources.Single(r => r.Id == "material").Material!.Textures[0].ResourceId);
    }

    [Theory]
    [InlineData("sampler")]
    [InlineData("load")]
    [InlineData("name")]
    [InlineData("assessment")]
    [InlineData("review")]
    public void ChangedOrUnreviewedContractIsRejected(string change)
    {
        var (package, receipt) = Create();
        receipt = change switch
        {
            "sampler" => receipt with { SamplerState = 9 },
            "load" => receipt with { LoadFlags = 9 },
            "name" => receipt with { RequestedName = receipt.RequestedName with { Name = "other.dds" } },
            "assessment" => receipt with { ContractAssessmentSha256 = "invalid" },
            _ => receipt with { Reviewed = false },
        };
        Assert.ThrowsAny<Exception>(() => CharacterTextureFallbackAuthoring.Review(package, receipt));
    }

    [Fact]
    public void ChangedProviderStringCannotBeApprovedByMatchingWholeProviderHash()
    {
        var (package, receipt) = Create();
        var reviewed = CharacterTextureFallbackAuthoring.Review(package, receipt);
        var inventory = reviewed.Document.CharacterResources!;
        var provider = inventory.Resources.Single(r => r.Id == "material-provider");
        byte[] bytes = reviewed.CompanionPayloads[provider.EntryPath!].ToArray();
        bytes[(int)receipt.RequestedName.PayloadOffset] ^= 1;
        reviewed = reviewed with { CompanionPayloads = reviewed.CompanionPayloads.SetItem(provider.EntryPath!, ImmutableArray.Create(bytes)),
            Document = reviewed.Document with { CharacterResources = inventory with { Resources = inventory.Resources.Select(r => r.Id == provider.Id ? r with { ContentSha256 = Hash(bytes) } : r).ToImmutableArray() } } };
        Assert.ThrowsAny<Exception>(() => CharacterTextureFallbackAuthoring.Revalidate(reviewed));
    }

    [Fact]
    public void ApprovedMissingTexturePublicationAndMergeKeepOriginalMaterialAndRequestedString()
    {
        var (package, receipt) = Create();
        var reviewed = CharacterTextureFallbackAuthoring.Review(package, receipt);
        var material = reviewed.Document.CharacterResources!.Resources.Single(row => row.Id == receipt.MaterialResourceId);
        var provider = reviewed.Document.CharacterResources.Resources.Single(row => row.Id == receipt.ProviderResourceId);
        byte[] originalMaterial = reviewed.CompanionPayloads[material.EntryPath!].ToArray();
        var destination = PublicationMaterials(reviewed);
        var published = Dl1CompiledMaterialGraphMerger.Build(destination, reviewed.CompanionPayloads[provider.EntryPath!]);
        var verified = CharacterMaterialPublication.Verify(reviewed, published);
        Assert.Equal(1, verified.MaterialCount);
        Assert.Equal(1, verified.TextureReferenceCount);
        var merged = CharacterMaterialPublication.Merge(reviewed, destination);
        Assert.Single(merged.Plans);
        Assert.Equal(1, merged.Readback.MaterialCount);
        Assert.Equal(1, merged.Readback.TextureReferenceCount);
        foreach (var output in new[] { published, merged.Database })
        {
            var graph = Dl1CompiledMaterialGraphReader.Read(output);
            var record = Assert.Single(graph.Containers.Single(row => row.Name == "materials").Records);
            Assert.Equal(material.Material!.NameHash, record.Key);
            Assert.Equal(originalMaterial, record.LogicalBytes.ToArray());
            var name = Dl1CompiledMaterialStringTable.Read(output, receipt.TextureNameHash)!;
            Assert.Equal(receipt.RequestedName.Name, name.Value);
            Assert.Equal(receipt.RequestedName.PayloadSha256, name.LogicalSha256);
        }
        Assert.Equal(originalMaterial, reviewed.CompanionPayloads[material.EntryPath!].ToArray());
        Assert.Null(material.Material!.Textures[0].ResourceId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("case")]
    [InlineData("path")]
    public void PublicationRejectsMissingOrChangedReviewedTextureString(string change)
    {
        var (package, receipt) = Create();
        var reviewed = CharacterTextureFallbackAuthoring.Review(package, receipt);
        var materialGraph = PublicationMaterials(reviewed);
        var output = materialGraph;
        if (change != "missing")
        {
            string name = change == "case" ? receipt.RequestedName.Name.ToUpperInvariant() : "folder/" + receipt.RequestedName.Name;
            byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
            var strings = Dl1CompiledMaterialGraphMergerTests.Pack("strings", (receipt.TextureNameHash, bytes, bytes));
            output = Dl1CompiledMaterialGraphMerger.Build(materialGraph, strings);
        }
        Assert.Throws<InvalidDataException>(() => CharacterMaterialPublication.Verify(reviewed, output));
        CharacterTextureFallbackAuthoring.Revalidate(reviewed);
    }

    [Fact]
    public void StaleFallbackCannotVerifyOrMergeEvenWithMatchingOriginalPublishedBytes()
    {
        var (package, receipt) = Create();
        var reviewed = CharacterTextureFallbackAuthoring.Review(package, receipt);
        var destination = PublicationMaterials(reviewed);
        var provider = reviewed.Document.CharacterResources!.Resources.Single(row => row.Id == receipt.ProviderResourceId);
        var published = Dl1CompiledMaterialGraphMerger.Build(destination, reviewed.CompanionPayloads[provider.EntryPath!]);
        var stale = reviewed with
        {
            Document = reviewed.Document with
            {
                CharacterResources = reviewed.Document.CharacterResources! with
                { TextureFallbackReviews = [receipt with { SamplerState = receipt.SamplerState ^ 1 }] },
            },
        };
        Assert.Throws<ArgumentException>(() => CharacterMaterialPublication.Verify(stale, published));
        Assert.Throws<ArgumentException>(() => CharacterMaterialPublication.Merge(stale, destination));
        Assert.Equal(receipt.SamplerState, reviewed.Document.CharacterResources.TextureFallbackReviews[0].SamplerState);
    }

    private static ImmutableArray<byte> PublicationMaterials(CustomModelPackage package)
    {
        var material = package.Document.CharacterResources!.Resources.Single(row => row.Id == "material");
        byte[] bytes = package.CompanionPayloads[material.EntryPath!].ToArray();
        return Dl1CompiledMaterialGraphMergerTests.Pack("materials", (material.Material!.NameHash, bytes, bytes));
    }

    internal static (CustomModelPackage Package, CharacterTextureFallbackReceipt Receipt) Create()
    {
        var package = CharacterMaterialReceiptTests.Create();
        var inventory = package.Document.CharacterResources!;
        var material = inventory.Resources.Single(r => r.Id == "material");
        byte[] payload = package.CompanionPayloads[material.EntryPath!].ToArray();
        uint key = material.Material!.Textures[0].TextureNameHash;
        var providerBytes = Dl1CompiledMaterialGraphMergerTests.Pack("strings", (key, Encoding.ASCII.GetBytes("generic_texture.dds\0"), Encoding.ASCII.GetBytes("generic_texture.dds\0")));
        var stringRow = Dl1CompiledMaterialStringTable.Read(providerBytes, key)!;
        byte[] provider = new byte[providerBytes.Length + payload.Length];
        providerBytes.AsSpan().CopyTo(provider); payload.CopyTo(provider, providerBytes.Length);
        var name = new CharacterTextureNameReceipt { Name = stringRow.Value, TableIndex = stringRow.EntryIndex, PayloadOffset = stringRow.SourceOffset,
            ByteLength = stringRow.LogicalByteLength, PayloadSha256 = stringRow.LogicalSha256 };
        var missing = new CharacterResourceRecord { Id = "missing-texture", LogicalName = "generic_texture.dds", Subsystem = CharacterSubsystem.Textures,
            Status = CharacterDependencyStatus.Missing, ReferencedBy = [material.Id] };
        material = material with { Material = material.Material with { ProviderSha256 = Hash(provider), PayloadOffset = providerBytes.Length,
            Textures = [material.Material.Textures[0] with { ResourceId = null, NameSource = name }] } };
        inventory = inventory with { Resources = inventory.Resources.Where(r => r.Id != "texture").Select(r => r.Id == "material" ? material : r.Id == "material-provider" ?
            r with { ContentSha256 = Hash(provider), ByteLength = provider.Length } : r).Append(missing).ToImmutableArray() };
        package = package with { Document = package.Document with { CharacterResources = inventory },
            CompanionPayloads = package.CompanionPayloads.Remove("character/resources/texture.bin").SetItem("character/resources/provider.bin", ImmutableArray.Create(provider)) };
        return (package, new() { MissingResourceId = missing.Id, MaterialResourceId = material.Id, MaterialSha256 = material.ContentSha256!,
            ProviderResourceId = "material-provider", ProviderSha256 = Hash(provider), TextureIndex = 0, TextureNameHash = key,
            SamplerState = 5, LoadFlags = 7, RequestedName = name, ProfileModuleSha256 = new string('a', 64),
            ContractAssessmentSha256 = new string('b', 64), Reviewed = true });
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}




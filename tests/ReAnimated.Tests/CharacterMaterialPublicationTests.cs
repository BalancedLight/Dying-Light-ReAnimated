using System.Buffers.Binary;
using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterMaterialPublicationTests
{
    [Fact]
    public void ExactMaterialPayloadAndCompleteTextureCustodyPassPublicationReadback()
    {
        var package=CharacterMaterialReceiptTests.Create();
        var bytes=Database(package);
        var evidence=CharacterMaterialPublication.Verify(package,ImmutableArray.Create(bytes));
        Assert.Equal(1,evidence.MaterialCount);Assert.Equal(1,evidence.TextureReferenceCount);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("texture")]
    [InlineData("texture-name")]
    public void MissingChangedMaterialsOrIncompleteTextureCustodyAreRejected(string scenario)
    {
        var package=CharacterMaterialReceiptTests.Create();var bytes=Database(package);
        if(scenario=="missing")BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64),1);
        if(scenario=="changed")bytes[^1]^=1;
        if(scenario=="texture")
        {
            var inventory=package.Document.CharacterResources!;
            package=package with {Document=package.Document with {CharacterResources=inventory with
            {Resources=inventory.Resources.Select(resource=>resource.NativeResource is not null?resource with {NativeResource=null}:resource).ToImmutableArray()}}};
        }
        if(scenario=="texture-name")
        {
            var inventory=package.Document.CharacterResources!;
            package=package with {Document=package.Document with {CharacterResources=inventory with
            {Resources=inventory.Resources.Select(resource=>resource.NativeResource is not null?resource with
                {LogicalName="other_texture",NativeResource=resource.NativeResource with {ResourceName="other_texture"}}:resource).ToImmutableArray()}}};
        }
        Assert.Throws<InvalidDataException>(()=>CharacterMaterialPublication.Verify(package,ImmutableArray.Create(bytes)));
    }
    [Fact]
    public void MergeKeepsDestinationOpaqueRecordsAndAddsExactSelectedProvider()
    {
        var package=CharacterMaterialReceiptTests.Create();byte[] source=Database(package);
        var inventory=package.Document.CharacterResources!;var provider=inventory.Resources.Single(row=>row.Id=="material-provider");
        string hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(source));
        var material=inventory.Resources.Single(row=>row.Material is not null);
        package=package with {CompanionPayloads=package.CompanionPayloads.SetItem(provider.EntryPath!,ImmutableArray.Create(source)),
            Document=package.Document with {CharacterResources=inventory with {Resources=inventory.Resources.Select(row=>row.Id==provider.Id?row with {ByteLength=source.Length,ContentSha256=hash}:row.Id==material.Id?row with {Material=row.Material! with {ProviderSha256=hash,PayloadOffset=80}}:row).ToImmutableArray()}}};
        var destination=Dl1CompiledMaterialGraphMergerTests.Pack("future",(1,[7,8],[7,8,9]));
        var merged=CharacterMaterialPublication.Merge(package,destination);
        Assert.Equal(1,merged.Readback.MaterialCount);Assert.Equal(1,Assert.Single(merged.Plans).AddedContainers);
        var graph=ReAnimated.Codecs.Materials.Dl1CompiledMaterialGraphReader.Read(merged.Database);
        Assert.Equal(new byte[]{7,8,9},graph.Containers.Single(container=>container.Name=="future").Records[0].StoredBytes.ToArray());
        Assert.Equal(source.AsSpan(80).ToArray(),graph.Containers.Single(container=>container.Name=="materials").Records[0].LogicalBytes.ToArray());
    }

    internal static byte[] Database(CustomModelPackage package)
    {
        var resource=package.Document.CharacterResources!.Resources.Single(resource=>resource.Material is not null);
        byte[] material=package.CompanionPayloads[resource.EntryPath!].ToArray();byte[] bytes=new byte[80+material.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x4D444241);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4),1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8),16);"materials"u8.CopyTo(bytes.AsSpan(16));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48),1);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(52),1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56),64);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64),resource.Material!.NameHash);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(68),80);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(72),material.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(76),material.Length);material.CopyTo(bytes,80);return bytes;
    }
}

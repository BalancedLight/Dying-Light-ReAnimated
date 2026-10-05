using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.DL1.Assets.Materials;

namespace ReAnimated.Tests;

public sealed class CharacterMaterialReceiptTests
{
    [Fact]
    public void ExactProviderMaterialAndTextureRowsSurviveSaveRead()
    {
        var package=Create();
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path=Path.Combine(directory,"character.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(package,path);
            var reopened=CustomModelPackageSerializer.Load(path);
            var material=reopened.Document.CharacterResources!.Resources.Single(resource=>resource.Material is not null);
            Assert.Equal("generic.mat",material.Material!.MaterialName);
            Assert.Equal(Dl1ResourceNameHash.Compute("generic.mat"),material.Material.NameHash);
            Assert.Equal(7U,Assert.Single(material.Material.Textures).LoadFlags);
            Assert.Equal(package.CompanionPayloads[material.EntryPath!].ToArray(),reopened.CompanionPayloads[material.EntryPath!].ToArray());
        }
        finally {RpackTestData.DeleteTemporaryDirectory(directory);}
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("sampler")]
    [InlineData("namehash")]
    [InlineData("payload")]
    public void ChangedProviderProvenanceIsRejected(string change)
    {
        var package=Create();var inventory=package.Document.CharacterResources!;
        var row=inventory.Resources.Single(resource=>resource.Material is not null);var receipt=row.Material!;
        if(change=="offset") row=row with {Material=receipt with {PayloadOffset=receipt.PayloadOffset+1}};
        if(change=="sampler") row=row with {Material=receipt with {Textures=receipt.Textures.SetItem(0,receipt.Textures[0] with {SamplerState=9})}};
        if(change=="namehash") row=row with {Material=receipt with {NameHash=0}};
        if(change=="payload")
        {
            byte[] bytes=package.CompanionPayloads[row.EntryPath!].ToArray();bytes[^1]^=1;
            package=package with {CompanionPayloads=package.CompanionPayloads.SetItem(row.EntryPath!,ImmutableArray.Create(bytes))};
            row=row with {ContentSha256=Hash(bytes)};
        }
        package=package with {Document=package.Document with {CharacterResources=inventory with {Resources=inventory.Resources.Select(resource=>resource.Id==row.Id?row:resource).ToImmutableArray()}}};
        if(change=="namehash") Assert.Throws<ArgumentException>(()=>CustomModelPackageSerializer.Serialize(package));
        else Assert.ThrowsAny<FormatException>(()=>CustomModelPackageSerializer.Serialize(package));
    }

    [Fact]
    public void NativeTextureItemTamperingCannotPassWholeResourceHashAlone()
    {
        var package=Create();var inventory=package.Document.CharacterResources!;
        var texture=inventory.Resources.Single(resource=>resource.NativeResource is not null);
        byte[] changed=package.CompanionPayloads[texture.EntryPath!].ToArray();changed[^1]^=1;
        texture=texture with {ContentSha256=Hash(changed)};
        package=package with {CompanionPayloads=package.CompanionPayloads.SetItem(texture.EntryPath!,ImmutableArray.Create(changed)),
            Document=package.Document with {CharacterResources=inventory with {Resources=inventory.Resources.Select(resource=>resource.Id==texture.Id?texture:resource).ToImmutableArray()}}};
        Assert.ThrowsAny<FormatException>(()=>CustomModelPackageSerializer.Serialize(package));
    }

    [Fact]
    public void ReferenceAdoptionRemapsProviderAndTextureIdentities()
    {
        var reference=Create();
        var target=FbxModelAuthoringImporter.Import(ModelsWorkspaceMorphAuthoringTests.CreateGenericManualSculptFbx(0),"generic.fbx");
        var adopted=CharacterReferenceAuthoring.AdoptReference(target,FbxModelAuthoringImporter.ImportPackage(reference));
        var material=adopted.Package.Document.CharacterResources!.Resources.Single(resource=>resource.Material is not null);
        Assert.Equal("reference-resource:material-provider",material.Material!.ProviderResourceId);
        Assert.Equal("reference-resource:texture",Assert.Single(material.Material.Textures).ResourceId);
        Assert.NotNull(adopted.Package.Document.CharacterResources.Resources.Single(resource=>resource.Id=="reference-resource:texture").NativeResource);
        CustomModelPackageSerializer.Serialize(adopted.Package);
    }

    [Fact]
    public void DetachedReferenceCustodySurvivesButRequiresTargetAssociationReview()
    {
        var reference=Create();var inventory=reference.Document.CharacterResources!;
        var texture=inventory.Resources.Single(resource=>resource.NativeResource is not null);
        var mesh=texture with {Id="detached",LogicalName="generic_part",EntryPath="character/resources/part.bin",Subsystem=CharacterSubsystem.DetachedParts,
            NativeResource=texture.NativeResource! with {ResourceName="generic_part",ResourceType=272}};
        reference=reference with {Document=reference.Document with {CharacterResources=inventory with {Resources=inventory.Resources.Add(mesh)}},
            CompanionPayloads=reference.CompanionPayloads.Add(mesh.EntryPath!,reference.CompanionPayloads[texture.EntryPath!])};
        var target=FbxModelAuthoringImporter.Import(ModelsWorkspaceMorphAuthoringTests.CreateGenericManualSculptFbx(0),"generic.fbx");
        var adopted=CharacterReferenceAuthoring.AdoptReference(target,FbxModelAuthoringImporter.ImportPackage(reference));
        var retained=adopted.Package.Document.CharacterResources!.Resources.Single(resource=>resource.Id=="reference-resource:detached");
        Assert.Equal(272,retained.NativeResource!.ResourceType);Assert.Equal(CharacterDependencyStatus.Ambiguous,retained.Status);
        Assert.Equal(reference.CompanionPayloads[mesh.EntryPath!].ToArray(),adopted.Package.CompanionPayloads[retained.EntryPath!].ToArray());
        CustomModelPackageSerializer.Serialize(adopted.Package);
    }

    internal static CustomModelPackage Create()
    {
        var package=ModelsWorkspaceMorphAuthoringTests.CreateGenericDifferentTopologyTargetPackage();
        byte[] material=new byte[36];BinaryPrimitives.WriteUInt32LittleEndian(material,Dl1ResourceNameHash.Compute("generic.mat"));
        BinaryPrimitives.WriteUInt16LittleEndian(material.AsSpan(16),1);BinaryPrimitives.WriteUInt16LittleEndian(material.AsSpan(18),1);
        BinaryPrimitives.WriteUInt16LittleEndian(material.AsSpan(22),2);BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(24),5);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(28),Dl1ResourceNameHash.ComputeTextureResource("generic_texture"));BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(32),7);
        byte[] provider=new byte[80];material.CopyTo(provider,32);
        var archive=new CharacterResourceRecord{Id="material-provider",LogicalName="generic.mp",EntryPath="character/resources/provider.bin",ContentSha256=Hash(provider),ByteLength=provider.Length,Subsystem=CharacterSubsystem.Materials,Status=CharacterDependencyStatus.Preserved,Required=false};
        byte[] textureBytes=[1,2,3,4,5,6,7];
        var texture=new CharacterResourceRecord{Id="texture",LogicalName="generic_texture",EntryPath="character/resources/texture.bin",ContentSha256=Hash(textureBytes),ByteLength=textureBytes.Length,Subsystem=CharacterSubsystem.Textures,Status=CharacterDependencyStatus.Preserved,
            NativeResource=new(){HeaderVersion=1,HeaderUnknown=1,ResourceName="generic_texture",ResourceType=8480,SourceResourceIndex=2,Items=[new(3,0,0,2,0,32,514,1,2,0,3,Hash(textureBytes[..3]),new string('a',64)),new(4,1,0,2,0,33,514,1,2,3,4,Hash(textureBytes[3..]),new string('b',64))]}};
        var record=new CharacterResourceRecord{Id="material",LogicalName="generic.mat",EntryPath="character/resources/material.bin",ContentSha256=Hash(material),ByteLength=material.Length,Subsystem=CharacterSubsystem.Materials,Status=CharacterDependencyStatus.Preserved,
            Material=new(){ProviderResourceId=archive.Id,ProviderSha256=archive.ContentSha256!,MaterialName="generic.mat",NameHash=Dl1ResourceNameHash.Compute("generic.mat"),TableIndex=0,PayloadOffset=32,StoredByteLength=36,TechniqueCount=1,Textures=[new(0,5,Dl1ResourceNameHash.ComputeTextureResource("generic_texture"),7,texture.Id)]}};
        return package with {Document=package.Document with {CharacterResources=package.Document.CharacterResources! with {Resources=package.Document.CharacterResources.Resources.AddRange(new[]{archive,record,texture})}},CompanionPayloads=package.CompanionPayloads.Add(archive.EntryPath!,ImmutableArray.Create(provider)).Add(record.EntryPath!,ImmutableArray.Create(material)).Add(texture.EntryPath!,ImmutableArray.Create(textureBytes))};
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}

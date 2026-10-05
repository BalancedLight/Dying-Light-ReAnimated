using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Materials;

namespace ReAnimated.Tests;

public sealed class CharacterMaterialFallbackAuthoringTests
{
    [Fact]
    public void RootReviewRequiresEveryMatchingEntryAndKeepsOriginalRequestedData()
    {
        var package = Create();
        var original = package.Document.CharacterResources!;
        var first = Review(package, original.RootResourceId, 0);
        Assert.False(first.Document.CharacterResources!.HasApplicableMaterialFallback(Missing(first)));
        var complete = Review(first, original.RootResourceId, 1);
        var inventory = complete.Document.CharacterResources!;
        Assert.True(inventory.HasApplicableMaterialFallback(Missing(complete)));
        Assert.Equal(CharacterDependencyStatus.Missing, Missing(complete).Status);
        Assert.Equal(Missing(package).LogicalName, Missing(complete).LogicalName);
        Assert.Equal(original.OriginalMaterials.ToArray(), inventory.OriginalMaterials.ToArray());
        Assert.Equal(original.OriginalMaterialSlotCount, inventory.OriginalMaterialSlotCount);
        Assert.Equal(package.SourceFbx.ToArray(), complete.SourceFbx.ToArray());
        Assert.Equal(package.DecodedCharacterPayload.ToArray(), complete.DecodedCharacterPayload.ToArray());
        Assert.DoesNotContain(inventory.ExportBlockers, blocker => blocker.Contains("Missing base source.",StringComparison.Ordinal));
        Assert.Contains(inventory.ExportBlockers, blocker => blocker.Contains("Unrelated FX material.",StringComparison.Ordinal));
        Assert.False(inventory.IsGameReady);
        CharacterMaterialFallbackAuthoring.Revalidate(complete);
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path=Path.Combine(directory,"reviewed.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(complete,path);
            var reopened=CustomModelPackageSerializer.Load(path);
            CharacterMaterialFallbackAuthoring.Revalidate(reopened);
            Assert.True(reopened.Document.CharacterResources!.HasApplicableMaterialFallback(Missing(reopened)));
            Assert.Equal(2,reopened.Document.CharacterResources.MaterialFallbackReviews.Length);
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }

    [Fact]
    public void DetachedReviewDecodesNativeSlotsAndCannotCoverUnreviewedRootConsumer()
    {
        var package=Create();
        var inventory=package.Document.CharacterResources!;
        var root=inventory.Resources.Single(resource=>resource.Id==inventory.RootResourceId);
        var detached=root with {Id="detached",LogicalName="generic_part",EntryPath="character/resources/detached.bin",
            Subsystem=CharacterSubsystem.DetachedParts,NativeResource=root.NativeResource! with {ResourceName="generic_part"}};
        package=package with {Document=package.Document with {CharacterResources=inventory with
        {Resources=inventory.Resources.Select(resource=>resource.Id=="missing-base"?resource with {ReferencedBy=[inventory.RootResourceId,detached.Id]}:resource).Append(detached).ToImmutableArray()}},
            CompanionPayloads=package.CompanionPayloads.Add(detached.EntryPath!,package.CompanionPayloads[root.EntryPath!])};
        var reviewed=Review(Review(package,detached.Id,0),detached.Id,1);
        Assert.False(reviewed.Document.CharacterResources!.HasApplicableMaterialFallback(Missing(reviewed)));
        reviewed=Review(Review(reviewed,inventory.RootResourceId,0),inventory.RootResourceId,1);
        Assert.True(reviewed.Document.CharacterResources!.HasApplicableMaterialFallback(Missing(reviewed)));
        CharacterMaterialFallbackAuthoring.Revalidate(reviewed);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("flags")]
    [InlineData("profile")]
    [InlineData("unreviewed")]
    [InlineData("default-missing")]
    [InlineData("provider-conflict")]
    [InlineData("consumer-bytes")]
    public void InapplicableOrChangedSourcesCannotCreateReview(string change)
    {
        var package=Create();var inventory=package.Document.CharacterResources!;
        var proposal=Proposal(package,inventory.RootResourceId,0);
        if(change=="metadata")
        {
            package=package with {Document=package.Document with {CharacterResources=inventory with
                {OriginalMaterials=inventory.OriginalMaterials.SetItem(0,inventory.OriginalMaterials[0] with {RawLoadValue=9})}}};
            proposal=proposal with {ExpectedOriginalLoadValue=9};
        }
        if(change=="flags") proposal=proposal with {ExpectedOriginalLoadValue=9};
        if(change=="profile") proposal=proposal with {Profile="unsupported-profile"};
        if(change=="unreviewed") proposal=proposal with {Reviewed=false};
        if(change=="default-missing")
            package=package with {Document=package.Document with {CharacterResources=inventory with
                {Resources=inventory.Resources.Select(resource=>resource.Id=="default-material"?resource with {Required=false}:resource).ToImmutableArray()}}};
        if(change=="provider-conflict")
        {
            var fallback=inventory.Resources.Single(resource=>resource.Id=="default-material");
            var other=fallback with {Id="other-default",EntryPath="character/resources/other-default.bin"};
            package=package with {Document=package.Document with {CharacterResources=inventory with {Resources=inventory.Resources.Add(other)}},
                CompanionPayloads=package.CompanionPayloads.Add(other.EntryPath!,package.CompanionPayloads[fallback.EntryPath!])};
        }
        if(change=="consumer-bytes")
        {
            var root=inventory.Resources.Single(resource=>resource.Id==inventory.RootResourceId);
            byte[] bytes=package.CompanionPayloads[root.EntryPath!].ToArray();bytes[^1]^=1;
            package=package with {CompanionPayloads=package.CompanionPayloads.SetItem(root.EntryPath!,ImmutableArray.Create(bytes))};
        }
        var error=Record.Exception(()=>CharacterMaterialFallbackAuthoring.Review(package,proposal));
        Assert.NotNull(error);
        Assert.Empty(package.Document.CharacterResources!.MaterialFallbackReviews);
        Assert.False(package.Document.CharacterResources.HasApplicableMaterialFallback(Missing(package)));
    }

    [Fact]
    public void ProviderMutationAfterReviewFailsExportRevalidation()
    {
        var reviewed=Review(Review(Create(),"root",0),"root",1);
        var provider=reviewed.Document.CharacterResources!.Resources.Single(resource=>resource.Id=="default-provider");
        byte[] bytes=reviewed.CompanionPayloads[provider.EntryPath!].ToArray();bytes[^1]^=1;
        var changed=reviewed with {CompanionPayloads=reviewed.CompanionPayloads.SetItem(provider.EntryPath!,ImmutableArray.Create(bytes))};
        Assert.ThrowsAny<Exception>(()=>CharacterMaterialFallbackAuthoring.Revalidate(changed));
        var inventory=reviewed.Document.CharacterResources!;
        var missing=Missing(reviewed);
        var mutated=reviewed with {Document=reviewed.Document with {CharacterResources=inventory with
        {OriginalMaterials=inventory.OriginalMaterials.SetItem(0,inventory.OriginalMaterials[0] with {RawLoadValue=9})}}};
        Assert.False(mutated.Document.CharacterResources!.HasApplicableMaterialFallback(missing));
        Assert.ThrowsAny<Exception>(()=>CharacterMaterialFallbackAuthoring.Revalidate(mutated));
    }

    private static CustomModelPackage Review(CustomModelPackage package,string consumer,int index) =>
        CharacterMaterialFallbackAuthoring.Review(package,Proposal(package,consumer,index));

    private static ReviewedCharacterMaterialFallback Proposal(CustomModelPackage package,string consumer,int index)
    {
        var inventory=package.Document.CharacterResources!;
        var source=inventory.Resources.Single(resource=>resource.Id==consumer);
        return new("missing-base",consumer,index,index==0?0x11U:0U,source.ContentSha256!,"default-material",
            new string('a',64),new string('b',64),true);
    }

    private static CharacterResourceRecord Missing(CustomModelPackage package) =>
        package.Document.CharacterResources!.Resources.Single(resource=>resource.Id=="missing-base");

    private static CustomModelPackage Create()
    {
        var package=ModelsWorkspaceMorphAuthoringTests.CreateGenericDifferentTopologyTargetPackage();
        var model=FbxModelAuthoringImporter.ImportPackage(package);
        var fixture=RpackTestData.BuildCompiledMeshFixture();
        byte[] metadata=(byte[])fixture.Metadata.Clone();
        metadata.AsSpan(RpackTestData.CompiledMeshMaterialDatabaseEntriesOffset,8)
            .CopyTo(metadata.AsSpan(RpackTestData.CompiledMeshMaterialDatabaseEntriesOffset+24,8));
        var decoded=CompiledMeshGeometryDecoder.Decode(metadata,fixture.Variants,fixture.Vertices,fixture.Indices,retailResourceName:"generic_root");
        byte[][] parts=[metadata,fixture.Variants,[1,2,3],fixture.Vertices,fixture.Indices];
        byte[] nativeBytes=parts.SelectMany(value=>value).ToArray();int offset=0;
        var items=parts.Select((bytes,index)=>{var item=new CharacterNativeItemReceipt(index,0,0,2,0,16,514,1,2,offset,bytes.Length,Hash(bytes),new string('c',64));offset+=bytes.Length;return item;}).ToImmutableArray();
        var inventory=package.Document.CharacterResources!;
        var root=inventory.Resources.Single(resource=>resource.Id==inventory.RootResourceId) with
        {Id="root",LogicalName="generic_root",ByteLength=nativeBytes.Length,ContentSha256=Hash(nativeBytes),
            NativeResource=new(){HeaderVersion=1,ResourceName="generic_root",ResourceType=272,Items=items}};
        byte[] material=new byte[24];BinaryPrimitives.WriteUInt32LittleEndian(material,Dl1ResourceNameHash.Compute("default.mat"));
        BinaryPrimitives.WriteUInt16LittleEndian(material.AsSpan(16),1);BinaryPrimitives.WriteUInt16LittleEndian(material.AsSpan(22),2);
        byte[] provider=new byte[64];material.CopyTo(provider,32);
        var providerRecord=new CharacterResourceRecord{Id="default-provider",LogicalName="generic.mp",EntryPath="character/resources/default-provider.bin",
            ContentSha256=Hash(provider),ByteLength=provider.Length,Subsystem=CharacterSubsystem.Materials,Status=CharacterDependencyStatus.Preserved,Required=false};
        var fallback=new CharacterResourceRecord{Id="default-material",LogicalName="default.mat",EntryPath="character/resources/default-material.bin",
            ContentSha256=Hash(material),ByteLength=material.Length,Subsystem=CharacterSubsystem.Materials,Status=CharacterDependencyStatus.Preserved,Required=true,
            Material=new(){ProviderResourceId=providerRecord.Id,ProviderSha256=providerRecord.ContentSha256!,MaterialName="default.mat",
                NameHash=Dl1ResourceNameHash.Compute("default.mat"),TableIndex=0,PayloadOffset=32,StoredByteLength=24,TechniqueCount=1}};
        var missing=new CharacterResourceRecord{Id="missing-base",LogicalName=decoded.MaterialDatabase.Entries[0].DatabaseName,
            Subsystem=CharacterSubsystem.Materials,Status=CharacterDependencyStatus.Missing,ReferencedBy=[root.Id],Detail="Missing base source."};
        var unrelated=missing with {Id="missing-fx",LogicalName="unrelated_fx.mat",ReferencedBy=["fx-consumer"],Detail="Unrelated FX material."};
        inventory=inventory with {RootResourceId=root.Id,Resources=[root,providerRecord,fallback,missing,unrelated],
            OriginalMaterialSlotCount=decoded.MaterialDatabase.DeclaredSlotCount,
            OriginalMaterials=decoded.MaterialDatabase.Entries.Select(entry=>new CharacterOriginalMaterial(entry.Index,entry.DatabaseName,entry.RawLoadValue)).ToImmutableArray()};
        var document=package.Document with {Source=package.Document.Source with {ContentSha256=Hash(nativeBytes)},CharacterResources=inventory};
        var snapshot=DecodedCharacterSnapshotCodec.Encode(new(document,model.Surfaces,[]));
        document=document with {CharacterResources=inventory with {DecodedByteLength=snapshot.Length,DecodedSha256=Hash(snapshot.ToArray())}};
        return package with {Document=document,SourceFbx=ImmutableArray.Create(nativeBytes),DecodedCharacterPayload=snapshot,
            CompanionPayloads=ImmutableDictionary<string,ImmutableArray<byte>>.Empty
                .Add(root.EntryPath!,ImmutableArray.Create(nativeBytes)).Add(providerRecord.EntryPath!,ImmutableArray.Create(provider))
                .Add(fallback.EntryPath!,ImmutableArray.Create(material))};
    }

    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterPackedEffectReceiptTests
{
    [Fact]
    public void ExactBundleSourceSurvivesSaveReadAndReferenceAdoption()
    {
        var package=Create();
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path=Path.Combine(directory,"character.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(package,path);
            var reopened=CustomModelPackageSerializer.Load(path);
            var effect=reopened.Document.CharacterResources!.Resources.Single(resource=>resource.Id=="packed-effect");
            Assert.Equal(6,effect.PackedEffect!.Kind);
            Assert.Equal(package.CompanionPayloads[effect.EntryPath!].ToArray(),reopened.CompanionPayloads[effect.EntryPath!].ToArray());
            var target=CustomModelPreviewSessionTests.CreateModel(false);
            var sourceHash=Convert.ToHexStringLower(SHA256.HashData(target.Package.SourceFbx.AsSpan()));
            target=target with {Package=target.Package with {Document=target.Package.Document with {Source=target.Package.Document.Source with {ContentSha256=sourceHash}}}};
            var adopted=CharacterReferenceAuthoring.AdoptReference(target,FbxModelAuthoringImporter.ImportPackage(reopened));
            var adoptedEffect=adopted.Package.Document.CharacterResources!.Resources.Single(resource=>resource.Id=="reference-resource:packed-effect");
            Assert.Equal("reference-resource:packed-bundle",adoptedEffect.PackedEffect!.BundleResourceId);
            CustomModelPackageSerializer.Serialize(adopted.Package);
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }
    [Theory]
    [InlineData("kind")]
    [InlineData("offset")]
    [InlineData("text")]
    public void AlteredPackedProvenanceCannotBeSerialized(string change)
    {
        var package=Create();var inventory=package.Document.CharacterResources!;
        var effect=inventory.Resources.Single(resource=>resource.Id=="packed-effect");
        var receipt=effect.PackedEffect!;
        if(change=="kind") effect=effect with {PackedEffect=receipt with {Kind=2}};
        if(change=="offset") effect=effect with {PackedEffect=receipt with {TextOffset=receipt.TextOffset+1,EntryByteLength=receipt.EntryByteLength+1}};
        if(change=="text")
        {
            byte[] changed=Encoding.UTF8.GetBytes("SequenceDef() { Unknown(8) }\n");
            effect=effect with {ContentSha256=Convert.ToHexStringLower(SHA256.HashData(changed))};
            package=package with {CompanionPayloads=package.CompanionPayloads.SetItem(effect.EntryPath!,ImmutableArray.Create(changed))};
        }
        package=package with {Document=package.Document with {CharacterResources=inventory with {Resources=inventory.Resources.Select(resource=>resource.Id==effect.Id?effect:resource).ToImmutableArray()}}};
        if(change=="offset")
            Assert.Throws<ArgumentException>(()=>CustomModelPackageSerializer.Serialize(package));
        else
            Assert.ThrowsAny<FormatException>(()=>CustomModelPackageSerializer.Serialize(package));
    }
    private static CustomModelPackage Create()
    {
        var package=ModelsWorkspaceMorphAuthoringTests.CreateGenericDifferentTopologyTargetPackage();
        var text=Encoding.UTF8.GetBytes("SequenceDef() { Unknown(7) }\n");
        using var output=new MemoryStream();output.Write(Encoding.UTF8.GetBytes("generic_sequence"));output.WriteByte(0);output.WriteByte(6);int offset=(int)output.Position;output.Write(text);output.WriteByte(0);int length=(int)output.Length;output.WriteByte(0);byte[] bundle=output.ToArray();
        string bundleHash=Convert.ToHexStringLower(SHA256.HashData(bundle));
        var original=new CharacterResourceRecord{Id="packed-bundle",LogicalName="original-bundle.bin",EntryPath="character/resources/packed-bundle.bin",ProviderIdentity="generic-rpack",SourceFingerprint=new string('a',64),ContentSha256=bundleHash,ByteLength=bundle.Length,Subsystem=CharacterSubsystem.Damage,Status=CharacterDependencyStatus.Preserved,Required=false,IsOriginalArchive=true};
        var effect=new CharacterResourceRecord{Id="packed-effect",LogicalName="generic_sequence.fx",EntryPath="character/resources/packed-effect.bin",ProviderIdentity="generic-rpack-effects",SourceFingerprint=new string('a',64),ContentSha256=Convert.ToHexStringLower(SHA256.HashData(text)),ByteLength=text.Length,Subsystem=CharacterSubsystem.Damage,Status=CharacterDependencyStatus.Preserved,PackedEffect=new(){BundleResourceId=original.Id,BundleSha256=bundleHash,StoredName="generic_sequence",Kind=6,ResourceIndex=1,ItemIndex=2,ChunkIndex=0,EntryOffset=0,EntryByteLength=length,TextOffset=offset,TextByteLength=text.Length}};
        return package with {Document=package.Document with {CharacterResources=package.Document.CharacterResources! with {Resources=package.Document.CharacterResources.Resources.AddRange(new[]{original,effect})}},CompanionPayloads=package.CompanionPayloads.Add(original.EntryPath!,ImmutableArray.Create(bundle)).Add(effect.EntryPath!,ImmutableArray.Create(text))};
    }
}
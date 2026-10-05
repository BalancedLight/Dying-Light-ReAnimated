using System.Buffers.Binary;
using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterCompiledResourceAuthoringTests
{
    [Fact]
    public void OldCompilerReceiptCannotClaimFullNativeResourceReadback()
    {
        var model=FbxModelAuthoringImporter.ImportPackage(CharacterMaterialReceiptTests.Create());
        var receipt=new CustomModelBuildReceipt{State=CustomModelBuildState.CompilerValidated,ToolFingerprint=new string('a',64),
            InputFingerprint=new string('b',64),OutputManifestFingerprint=new string('c',64),CompletedUtc=DateTimeOffset.UtcNow};
        Assert.False(Dl1OfficialModelCompiler.IsCurrentBuildReceipt(receipt,model));
    }

    [Theory]
    [InlineData("texture")]
    [InlineData("effects")]
    [InlineData("detached")]
    public async Task LinksAndReadsEveryRetainedNativeResourceItem(string kind)
    {
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            bool effects=kind=="effects";bool detached=kind=="detached";
            var package=effects?CharacterEffectExportCommandTests.CreatePackage():CharacterMaterialReceiptTests.Create();
            if(detached)
            {
                var inventory=package.Document.CharacterResources!;
                package=package with {Document=package.Document with {CharacterResources=inventory with
                {Resources=inventory.Resources.Select(resource=>resource.NativeResource is not null?resource with
                    {LogicalName="generic_part",Subsystem=CharacterSubsystem.DetachedParts,NativeResource=resource.NativeResource with {ResourceName="generic_part",ResourceType=272}}:
                    resource.Material is not null?resource with {Required=false,Material=resource.Material with {Textures=[]}}:resource).ToImmutableArray()}}};
                // Remove unrelated material receipt/payload; this fixture exercises opaque item linking only.
                var material=package.Document.CharacterResources!.Resources.Single(resource=>resource.Material is not null);
                package=package with {Document=package.Document with {CharacterResources=package.Document.CharacterResources with
                    {Resources=package.Document.CharacterResources.Resources.Where(resource=>resource.Id!=material.Id).ToImmutableArray()}},
                    CompanionPayloads=package.CompanionPayloads.Remove(material.EntryPath!)};
            }
            await using var cache=new Rp6lChunkCache(new(){CacheDirectory=Path.Combine(directory,"cache")});
            var built=await CharacterCompiledResourceAuthoring.WriteObjectsAsync(package,Path.Combine(directory,"companions"),cache);
            string compiler=Path.Combine(directory,"generic.msh_obj");
            byte[] mesh=RpackTestData.BuildArchive("generic_mesh",unchecked((short)0x8110),[new(1,[1,2,3])],RpackTestCompression.None);
            BinaryPrimitives.WriteUInt32LittleEndian(mesh.AsSpan(40),0);
            int tableEnd=mesh.Length-3;BinaryPrimitives.WriteUInt32LittleEndian(mesh.AsSpan(60),checked((uint)tableEnd));
            await File.WriteAllBytesAsync(compiler,mesh);
            string linked=Path.Combine(directory,"character.rpack");
            await Rp6lCompilerObjectNormalizer.LinkAtomicAsync([compiler,..built.ObjectPaths],linked);
            var archive=await Rp6lArchive.OpenAsync(linked);
            var evidence=await CharacterCompiledResourceAuthoring.VerifyLinkedAsync(package,archive,cache);
            Assert.Equal(effects?2:0,evidence.EffectCount);
            Assert.Equal(!effects&&!detached?1:0,evidence.TextureCount);
            Assert.Equal(!effects&&!detached?2:0,evidence.TextureItemCount);
            Assert.Equal(detached?1:0,evidence.DetachedMeshCount);
            Assert.Equal(detached?2:0,evidence.DetachedMeshItemCount);
            Assert.Equal(built.EffectCount,evidence.EffectCount);
            Assert.Equal(built.TextureItemCount,evidence.TextureItemCount);
        }
        finally {RpackTestData.DeleteTemporaryDirectory(directory);}
    }
}

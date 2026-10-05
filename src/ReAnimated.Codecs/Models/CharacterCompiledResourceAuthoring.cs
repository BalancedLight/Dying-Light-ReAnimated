using System.Collections.Immutable;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record CharacterCompiledResourceBuild(ImmutableArray<string> ObjectPaths,
    int EffectCount,int TextureCount,int TextureItemCount)
{
    public int DetachedMeshCount {get;init;}
    public int DetachedMeshItemCount {get;init;}
    public ImmutableDictionary<string,string> DetachedObjectPaths {get;init;}=ImmutableDictionary<string,string>.Empty;
}
public sealed record CharacterCompiledResourceReadback(int EffectCount,int TextureCount,int TextureItemCount)
{
    public int DetachedMeshCount {get;init;}
    public int DetachedMeshItemCount {get;init;}
}

public static class CharacterCompiledResourceAuthoring
{
    public static async Task<CharacterCompiledResourceBuild> WriteObjectsAsync(CustomModelPackage package,
        string directory,Rp6lChunkCache cache,CancellationToken cancellationToken=default)
    {
        var effects=CharacterEffectResourceAuthoring.ReadRequiredDefinitions(package,cancellationToken);
        var paths=ImmutableArray.CreateBuilder<string>();
        var detachedPaths=ImmutableDictionary.CreateBuilder<string,string>(StringComparer.OrdinalIgnoreCase);
        if(!effects.IsEmpty)
        {
            string path=Path.Combine(directory,"character-effects.rpack");
            await Rp6lEffectBundleWriter.WriteNewAsync(path,effects,Rp6lCompression.None,cache,cancellationToken).ConfigureAwait(false);
            paths.Add(path);
        }
        int textureCount=0,itemCount=0,detachedCount=0,detachedItems=0;
        foreach(var row in (package.Document.CharacterResources?.Resources ?? []).Where(row=>row.Required && !row.IsOriginalArchive && row.Id != package.Document.CharacterResources!.RootResourceId && row.NativeResource is not null)
            .OrderBy(row=>row.LogicalName,StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(row.Status is not (CharacterDependencyStatus.Preserved or CharacterDependencyStatus.Decoded))
                throw new InvalidDataException("Resolve the selected native character resource before compiling: "+row.LogicalName);
            var receipt=row.NativeResource!;
            var bytes=package.CompanionPayloads[row.EntryPath!];
            var items=receipt.Items.Select((item,index)=>new Rp6lResourceItemPayload(
                new(index,index,item.Flags,item.StorageGroupId,0,item.ByteLength,item.Unknown),
                bytes.AsSpan(checked((int)item.PayloadOffset),item.ByteLength).ToArray().ToImmutableArray(),item.ContentSha256)).ToImmutableArray();
            var chunks=receipt.Items.Select((item,index)=>new Rp6lChunkDescriptor(index,item.ChunkFlags,item.ChunkCategory,0,item.ByteLength,0,
                item.ChunkUnknown0,item.ChunkUnknown1,Rp6lCompression.None)).ToImmutableArray();
            var resource=new Rp6lResourceDescriptor(0,receipt.ResourceName,receipt.ResourceType,0,0,items.Length,items.Select(item=>item.Descriptor).ToImmutableArray());
            var header=new Rp6lHeader(receipt.HeaderVersion,0,items.Length,items.Length,1,receipt.ResourceName.Length+1,1,receipt.HeaderUnknown);
            string kind=receipt.ResourceType==Rp6lResourceTypes.Mesh?"detached":"texture";
            int resourceNumber=receipt.ResourceType==Rp6lResourceTypes.Mesh?detachedCount:textureCount;
            string path=Path.Combine(directory,$"character-{kind}-{resourceNumber:D5}.rpack");
            await Rp6lResourceEnvelopeWriter.WriteNewAsync(path,header,resource,items,chunks,cache,cancellationToken).ConfigureAwait(false);
            paths.Add(path);
            if(receipt.ResourceType==Rp6lResourceTypes.Mesh){if(!detachedPaths.TryAdd(receipt.ResourceName,path))throw new InvalidDataException("Detached resource names are ambiguous.");detachedCount++;detachedItems+=items.Length;}
            else{textureCount++;itemCount+=items.Length;}
        }
        return new(paths.ToImmutable(),effects.Length,textureCount,itemCount){DetachedMeshCount=detachedCount,DetachedMeshItemCount=detachedItems,DetachedObjectPaths=detachedPaths.ToImmutable()};
    }

    public static async Task<CharacterCompiledResourceReadback> VerifyLinkedAsync(CustomModelPackage package,
        Rp6lArchive archive,Rp6lChunkCache cache,CancellationToken cancellationToken=default)
    {
        var expected=CharacterEffectResourceAuthoring.ReadRequiredDefinitions(package,cancellationToken);
        if(!expected.IsEmpty)
        {
            var fx=archive.Resources.Where(resource=>resource.ResourceType==Rp6lResourceTypes.Effect).ToArray();
            if(fx.Length!=1 || fx[0].Name!="FX" || fx[0].Items.Count!=1 || (archive.Chunks[fx[0].Items[0].ChunkIndex].Flags&255)!=80)
                throw new InvalidDataException("Linked character effects have invalid routing.");
            var actual=Rp6lEffectBundleDecoder.Decode(await archive.ReadItemBytesAsync(fx[0].Items[0],cache,Rp6lEffectBundleDecoder.MaximumBytes,cancellationToken).ConfigureAwait(false),cancellationToken);
            if(!actual.Select(value=>(value.Name,value.Kind,value.SourceText,value.ContentSha256)).SequenceEqual(expected.Select(value=>(value.Name,value.Kind,value.SourceText,value.ContentSha256))))
                throw new InvalidDataException("Linked character effects differ from the original definitions.");
        }
        int textures=0,items=0,meshes=0,meshItems=0;
        foreach(var row in (package.Document.CharacterResources?.Resources ?? []).Where(row=>row.Required && !row.IsOriginalArchive && row.Id != package.Document.CharacterResources!.RootResourceId && row.NativeResource is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt=row.NativeResource!;
            var matches=archive.Resources.Where(resource=>resource.ResourceType==receipt.ResourceType && resource.Name.Equals(receipt.ResourceName,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length!=1 || matches[0].Items.Count!=receipt.Items.Length)
                throw new InvalidDataException("Linked character resource is missing or ambiguous.");
            for(int index=0;index<receipt.Items.Length;index++)
            {
                var source=receipt.Items[index];var item=matches[0].Items[index];var chunk=archive.Chunks[item.ChunkIndex];
                byte[] bytes=await archive.ReadItemBytesAsync(item,cache,64*1024*1024,cancellationToken).ConfigureAwait(false);
                if(item.Flags!=source.Flags || item.StorageGroupId!=source.StorageGroupId || item.Unknown!=source.Unknown ||
                    chunk.Flags!=source.ChunkFlags || chunk.Category!=source.ChunkCategory || chunk.Unknown0!=source.ChunkUnknown0 || chunk.Unknown1!=source.ChunkUnknown1 ||
                    bytes.Length!=source.ByteLength || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))!=source.ContentSha256)
                    throw new InvalidDataException("Linked character resource items differ from their original resource.");
                if(receipt.ResourceType==Rp6lResourceTypes.Mesh)meshItems++;else items++;
            }
            if(receipt.ResourceType==Rp6lResourceTypes.Mesh)meshes++;else textures++;
        }
        return new(expected.Length,textures,items){DetachedMeshCount=meshes,DetachedMeshItemCount=meshItems};
    }
}


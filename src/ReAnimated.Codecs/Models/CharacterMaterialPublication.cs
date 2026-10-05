using System.Collections.Immutable;
using ReAnimated.Codecs.Materials;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record CharacterMaterialPublicationReadback(string DatabaseSha256,int MaterialCount,int TextureReferenceCount);

public sealed record CharacterMaterialPublicationMerge(ImmutableArray<byte> Database,
    ImmutableArray<Dl1MaterialGraphMergePlan> Plans,CharacterMaterialPublicationReadback Readback);

public static class CharacterMaterialPublication
{
    public static CharacterMaterialPublicationMerge Merge(CustomModelPackage package,ImmutableArray<byte> destination,
        CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(package);cancellationToken.ThrowIfCancellationRequested();
        _=CustomModelPackageSerializer.Serialize(package);
        if(destination.IsDefaultOrEmpty || destination.Length>256L*1024*1024)
            throw new InvalidDataException("Select the existing project material database before merging.");
        var inventory=package.Document.CharacterResources;
        var providers=(inventory?.Resources ?? []).Where(resource=>resource.Required && !resource.IsOriginalArchive && resource.Material is not null)
            .Select(resource=>resource.Material!.ProviderResourceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var result=destination;var plans=ImmutableArray.CreateBuilder<Dl1MaterialGraphMergePlan>();
        foreach(string id in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();var provider=inventory!.Resources.Single(resource=>resource.Id==id);
            var source=package.CompanionPayloads[provider.EntryPath!];var plan=Dl1CompiledMaterialGraphMerger.Inspect(result,source,cancellationToken);
            if(!plan.CanMerge) throw new InvalidDataException("Character material graph conflicts: "+string.Join("; ",plan.Conflicts.Take(16).Select(conflict=>$"{conflict.Container}:0x{conflict.Key:X8}")));
            if(plan.AddedRecords>0 || plan.AddedContainers>0)result=Dl1CompiledMaterialGraphMerger.Build(result,source,cancellationToken);
            plans.Add(plan);
        }
        return new(result,plans.ToImmutable(),Verify(package,result,cancellationToken));
    }

    public static CharacterMaterialPublicationReadback Verify(CustomModelPackage package,ImmutableArray<byte> database,
        CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        _=CustomModelPackageSerializer.Serialize(package);
        if(database.IsDefaultOrEmpty || database.Length>256L*1024*1024)
            throw new InvalidDataException("The published material database exceeds its bounds.");
        CharacterMaterialFallbackAuthoring.Revalidate(package);
        CharacterTextureFallbackAuthoring.Revalidate(package);
        var graph=Dl1CompiledMaterialGraphReader.Read(database,cancellationToken:cancellationToken);
        var container=graph.Containers.SingleOrDefault(value=>value.Name.Equals("materials",StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The published database has no materials container.");
        var records=container.Records.ToDictionary(value=>value.Key);
        Dl1CompiledMaterialStringTable? publishedStrings = null;
        int verified=0,textures=0;
        foreach(var resource in (package.Document.CharacterResources?.Resources ?? []).Where(resource=>resource.Required && !resource.IsOriginalArchive && resource.Material is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();var receipt=resource.Material!;
            if(!records.TryGetValue(receipt.NameHash,out var record) || record.LogicalByteLength!=resource.ByteLength ||
                !record.LogicalBytes.AsSpan().SequenceEqual(package.CompanionPayloads[resource.EntryPath!].AsSpan()))
                throw new InvalidDataException("The published database changed or omitted character material: "+resource.LogicalName);
            foreach(var texture in receipt.Textures)
            {
                if (package.Document.CharacterResources!.HasApplicableTextureFallback(resource, texture))
                {
                    publishedStrings ??= Dl1CompiledMaterialStringTable.Open(database, cancellationToken: cancellationToken);
                    var requested = texture.NameSource;
                    var published = publishedStrings.Find(texture.TextureNameHash, cancellationToken);
                    if (requested is null || published is null || published.Value != requested.Name)
                        throw new InvalidDataException("The published database changed or omitted the reviewed texture name.");
                    continue;
                }
                var target=package.Document.CharacterResources!.Resources.SingleOrDefault(candidate=>candidate.Id==texture.ResourceId);
                if(target is null || target.NativeResource is not {ResourceType:8480} || target.EntryPath is null ||
                    target.Status is not (CharacterDependencyStatus.Preserved or CharacterDependencyStatus.Decoded) ||
                    TextureHash(target.NativeResource.ResourceName)!=texture.TextureNameHash)
                    throw new InvalidDataException("A published character material lacks complete native texture custody.");
            }
            verified++;textures+=receipt.Textures.Length;
        }
        return new(graph.SourceSha256,verified,textures);
    }
    private static uint TextureHash(string name)
    {
        string file=name.Split('/')[^1].ToLowerInvariant();
        if(file.Any(value=>value>127))throw new InvalidDataException("Texture lookup names must be ASCII.");
        if(!file.EndsWith(".dds",StringComparison.Ordinal))file+=".dds";
        uint crc=0x811C9DC5U^uint.MaxValue;
        foreach(char letter in file)
        {
            crc^=(byte)letter;
            for(int bit=0;bit<8;bit++)crc=(crc>>1)^(0xEDB88320U & unchecked((uint)-(int)(crc&1)));
        }
        return crc^uint.MaxValue;
    }

}



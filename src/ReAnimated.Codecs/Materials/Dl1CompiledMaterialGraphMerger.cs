using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace ReAnimated.Codecs.Materials;

public sealed record Dl1MaterialGraphConflict(string Container,uint Key,string DestinationSha256,string SourceSha256);
public sealed record Dl1MaterialGraphMergePlan(string DestinationSha256,string SourceSha256,
    int AddedContainers,int AddedRecords,int IdenticalRecords,ImmutableArray<Dl1MaterialGraphConflict> Conflicts)
{
    public bool CanMerge=>Conflicts.IsEmpty;
}
public sealed record Dl1MaterialGraphMergeResult(string Path,string OutputSha256,Dl1MaterialGraphMergePlan Plan);

public static class Dl1CompiledMaterialGraphMerger
{
    public static Dl1MaterialGraphMergePlan Inspect(ImmutableArray<byte> destination,ImmutableArray<byte> source,
        CancellationToken cancellationToken=default)
    {
        var prior=Dl1CompiledMaterialGraphReader.Read(destination,cancellationToken:cancellationToken);
        var addition=Dl1CompiledMaterialGraphReader.Read(source,cancellationToken:cancellationToken);
        return Plan(prior,addition,cancellationToken);
    }

    public static ImmutableArray<byte> Build(ImmutableArray<byte> destination,ImmutableArray<byte> source,
        CancellationToken cancellationToken=default)
    {
        var prior=Dl1CompiledMaterialGraphReader.Read(destination,cancellationToken:cancellationToken);
        var addition=Dl1CompiledMaterialGraphReader.Read(source,cancellationToken:cancellationToken);
        var plan=Plan(prior,addition,cancellationToken);
        if(!plan.CanMerge)
            throw new InvalidDataException("Material graph conflicts: "+string.Join("; ",plan.Conflicts.Take(16).Select(conflict=>$"{conflict.Container}:0x{conflict.Key:X8}")));
        var containers=new List<(Dl1CompiledMaterialGraphContainer Container,SortedDictionary<uint,Dl1CompiledMaterialGraphRecord> Records)>();
        foreach(var container in prior.Containers)
            containers.Add((container,new(container.Records.ToDictionary(record=>record.Key))));
        foreach(var container in addition.Containers)
        {
            int index=containers.FindIndex(value=>value.Container.Name.Equals(container.Name,StringComparison.OrdinalIgnoreCase));
            if(index<0){containers.Add((container,new()));index=containers.Count-1;}
            foreach(var record in container.Records)
            {cancellationToken.ThrowIfCancellationRequested();containers[index].Records.TryAdd(record.Key,record);}
        }
        long totalRecords=containers.Sum(value=>(long)value.Records.Count);
        long tableLength=checked(16L+containers.Count*48L+totalRecords*16);
        long storedLength=containers.Sum(value=>value.Records.Values.Sum(record=>(long)record.StoredByteLength));
        if(containers.Count>128 || totalRecords>1_000_000 || tableLength>32L*1024*1024 || tableLength+storedLength>256L*1024*1024)
            throw new InvalidDataException("The merged material graph exceeds its bounds.");
        byte[] output=new byte[checked((int)(tableLength+storedLength))];var bytes=output.AsSpan();
        prior.HeaderBytes.AsSpan().CopyTo(bytes);BinaryPrimitives.WriteInt32LittleEndian(bytes[4..],containers.Count);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..],16);
        int recordCursor=16+containers.Count*48;int payloadCursor=checked((int)tableLength);
        for(int index=0;index<containers.Count;index++)
        {
            cancellationToken.ThrowIfCancellationRequested();var container=containers[index];
            var row=bytes.Slice(16+index*48,48);container.Container.RowBytes.AsSpan().CopyTo(row);
            BinaryPrimitives.WriteInt32LittleEndian(row[32..],container.Records.Count);
            BinaryPrimitives.WriteInt32LittleEndian(row[36..],container.Records.Count);
            BinaryPrimitives.WriteInt32LittleEndian(row[40..],recordCursor);
            foreach(var record in container.Records.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();var entry=bytes.Slice(recordCursor,16);record.RowBytes.AsSpan().CopyTo(entry);
                BinaryPrimitives.WriteInt32LittleEndian(entry[4..],payloadCursor);
                record.StoredBytes.AsSpan().CopyTo(bytes[payloadCursor..]);payloadCursor+=record.StoredByteLength;recordCursor+=16;
            }
        }
        var result=ImmutableArray.Create(output);var reopened=Dl1CompiledMaterialGraphReader.Read(result,cancellationToken:cancellationToken);
        Verify(prior,addition,reopened,cancellationToken);return result;
    }

    public static async Task<Dl1MaterialGraphMergeResult> WriteNewAsync(string path,ImmutableArray<byte> destination,
        ImmutableArray<byte> source,CancellationToken cancellationToken=default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);cancellationToken.ThrowIfCancellationRequested();
        string target=Path.GetFullPath(path);if(File.Exists(target)||Directory.Exists(target))throw new IOException("The material output already exists.");
        var plan=Inspect(destination,source,cancellationToken);var bytes=Build(destination,source,cancellationToken);
        string directory=Path.GetDirectoryName(target)??throw new InvalidOperationException("Material output has no parent directory.");
        Directory.CreateDirectory(directory);string temporary=Path.Combine(directory,$".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,128*1024,FileOptions.Asynchronous|FileOptions.WriteThrough))
            {await stream.WriteAsync(bytes.AsMemory(),cancellationToken).ConfigureAwait(false);await stream.FlushAsync(cancellationToken).ConfigureAwait(false);}
            var current=ImmutableArray.Create(await File.ReadAllBytesAsync(temporary,cancellationToken).ConfigureAwait(false));
            if(!current.AsSpan().SequenceEqual(bytes.AsSpan()))throw new InvalidDataException("The staged material graph changed.");
            _=Dl1CompiledMaterialGraphReader.Read(current,cancellationToken:cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();File.Move(temporary,target,overwrite:false);
            return new(target,Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan())),plan);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }

    private static Dl1MaterialGraphMergePlan Plan(Dl1CompiledMaterialGraphInventory destination,Dl1CompiledMaterialGraphInventory source,CancellationToken token)
    {
        var conflicts=ImmutableArray.CreateBuilder<Dl1MaterialGraphConflict>();int addedContainers=0,added=0,same=0;
        foreach(var container in source.Containers)
        {
            token.ThrowIfCancellationRequested();var target=destination.Containers.SingleOrDefault(value=>value.Name.Equals(container.Name,StringComparison.OrdinalIgnoreCase));
            if(target is null){addedContainers++;added+=container.Records.Length;continue;}
            var records=target.Records.ToDictionary(record=>record.Key);
            foreach(var record in container.Records)
            {
                token.ThrowIfCancellationRequested();
                if(!records.TryGetValue(record.Key,out var prior)){added++;continue;}
                if(prior.SemanticByteLength==record.SemanticByteLength && prior.LogicalBytes.AsSpan(0,prior.SemanticByteLength).SequenceEqual(record.LogicalBytes.AsSpan(0,record.SemanticByteLength)))same++;
                else conflicts.Add(new(container.Name,record.Key,prior.LogicalSha256,record.LogicalSha256));
            }
        }
        return new(destination.SourceSha256,source.SourceSha256,addedContainers,added,same,conflicts.ToImmutable());
    }
    private static void Verify(Dl1CompiledMaterialGraphInventory prior,Dl1CompiledMaterialGraphInventory addition,Dl1CompiledMaterialGraphInventory result,CancellationToken token)
    {
        if(!result.Containers.Take(prior.Containers.Length).Select(container=>container.Name)
            .SequenceEqual(prior.Containers.Select(container=>container.Name),StringComparer.Ordinal))
            throw new InvalidDataException("The destination container order changed.");
        var emitted=result.Containers.ToDictionary(container=>container.Name,container=>container.Records.ToDictionary(record=>record.Key),StringComparer.OrdinalIgnoreCase);
        var existing=prior.Containers.ToDictionary(container=>container.Name,container=>container.Records.ToDictionary(record=>record.Key),StringComparer.OrdinalIgnoreCase);
        foreach(var container in prior.Containers)
        {
            var actual=emitted[container.Name];
            foreach(var record in container.Records)
            {token.ThrowIfCancellationRequested();var target=actual[record.Key];if(target.LogicalSha256!=record.LogicalSha256 || target.StoredSha256!=record.StoredSha256)throw new InvalidDataException("The destination material graph changed.");}
        }
        foreach(var container in addition.Containers)
        {
            var actual=emitted[container.Name];existing.TryGetValue(container.Name,out var before);
            foreach(var record in container.Records)
            {
                token.ThrowIfCancellationRequested();var target=actual[record.Key];bool duplicate=before?.ContainsKey(record.Key)==true;
                if(duplicate?target.SemanticSha256!=record.SemanticSha256:target.LogicalSha256!=record.LogicalSha256 || target.StoredSha256!=record.StoredSha256)
                    throw new InvalidDataException("The source material graph was not preserved.");
            }
        }
    }
}

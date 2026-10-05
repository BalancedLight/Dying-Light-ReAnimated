using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using ReAnimated.Codecs.Materials;

namespace ReAnimated.Tests;

public sealed class Dl1CompiledMaterialGraphMergerTests
{
    [Fact]
    public void UnionPreservesDestinationStoredBytesAndAddsOpaqueSourceRecords()
    {
        var destination=Pack("future",(1,[1,2,3],[1,2,3,7,8]));
        var source=Pack("future",(1,[1,2,3],[1,2,3,9]),(2,[4,5],[4,5,6]));
        var plan=Dl1CompiledMaterialGraphMerger.Inspect(destination,source);
        Assert.True(plan.CanMerge);Assert.Equal(1,plan.IdenticalRecords);Assert.Equal(1,plan.AddedRecords);
        var merged=Dl1CompiledMaterialGraphReader.Read(Dl1CompiledMaterialGraphMerger.Build(destination,source));
        Assert.Equal(new byte[]{1,2,3,7,8},merged.Containers[0].Records[0].StoredBytes.ToArray());
        Assert.Equal(new byte[]{4,5,6},merged.Containers[0].Records[1].StoredBytes.ToArray());
    }
    [Fact]
    public void ExistingContainerSpellingAndOrderRemainAheadOfNewContainers()
    {
        var first=Pack("Future",(1,[1],[1]));var appended=Pack("another",(2,[2],[2]));
        var merged=Dl1CompiledMaterialGraphReader.Read(Dl1CompiledMaterialGraphMerger.Build(first,appended));
        Assert.Equal("Future",merged.Containers[0].Name);Assert.Equal("another",merged.Containers[1].Name);
        Assert.Equal(first.AsSpan(16,32).ToArray(),merged.Containers[0].RowBytes.AsSpan(0,32).ToArray());
    }
    [Theory]
    [InlineData("strings")]
    [InlineData("input_attributes")]
    public void OnlyKnownSemanticZeroPaddingMayDiffer(string container)
    {
        byte[] plain=container=="strings"?[97,0]:[1,1,0,0,0];
        byte[] padded=container=="strings"?[97,0,0,0]:[1,1,0,0,0,0,0,0];
        var destination=Pack(container,(7,plain,plain));var source=Pack(container,(7,padded,padded));
        var merged=Dl1CompiledMaterialGraphReader.Read(Dl1CompiledMaterialGraphMerger.Build(destination,source));
        Assert.Equal(plain,merged.Containers[0].Records[0].LogicalBytes.ToArray());
    }
    [Theory]
    [InlineData("templates_0000")]
    [InlineData("future")]
    public void ConflictingKeysHaveReviewableIdentitiesAndCannotPublish(string container)
    {
        var destination=Pack(container,(9,[1,2],[1,2]));var source=Pack(container,(9,[1,3],[1,3]));
        var plan=Dl1CompiledMaterialGraphMerger.Inspect(destination,source);
        Assert.False(plan.CanMerge);var conflict=Assert.Single(plan.Conflicts);Assert.Equal(container,conflict.Container);Assert.Equal(9U,conflict.Key);
        Assert.NotEqual(conflict.DestinationSha256,conflict.SourceSha256);
        Assert.Throws<InvalidDataException>(()=>Dl1CompiledMaterialGraphMerger.Build(destination,source));
    }
    [Fact]
    public async Task NewOutputIsReadBackAndExistingDestinationIsNeverOverwritten()
    {
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path=Path.Combine(directory,"merged.mp");var prior=Pack("future",(1,[1],[1]));var addition=Pack("another",(2,[2],[2]));
            var result=await Dl1CompiledMaterialGraphMerger.WriteNewAsync(path,prior,addition);
            Assert.Equal(1,result.Plan.AddedContainers);byte[] before=await File.ReadAllBytesAsync(path);
            Assert.Equal(2,Dl1CompiledMaterialGraphReader.Read(ImmutableArray.Create(before)).TotalRecords);
            await Assert.ThrowsAsync<IOException>(()=>Dl1CompiledMaterialGraphMerger.WriteNewAsync(path,prior,addition));
            Assert.Equal(before,await File.ReadAllBytesAsync(path));Assert.Empty(Directory.GetFiles(directory,"*.tmp"));
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Dl1CompiledMaterialGraphMerger.WriteNewAsync(Path.Combine(directory,"cancel.mp"),prior,addition,cancel.Token));
            Assert.False(File.Exists(Path.Combine(directory,"cancel.mp")));
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }
    internal static ImmutableArray<byte> Pack(string name,params (uint Key,byte[] Logical,byte[] Stored)[] records)
    {
        int end=64+records.Length*16;byte[] bytes=new byte[end+records.Sum(record=>record.Stored.Length)];
        "ABDM"u8.CopyTo(bytes);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4),1);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8),16);
        Encoding.ASCII.GetBytes(name).CopyTo(bytes,16);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48),records.Length);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(52),records.Length);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56),64);
        int offset=end;
        for(int i=0;i<records.Length;i++)
        {
            var record=records[i];int row=64+i*16;BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(row),record.Key);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(row+4),offset);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(row+8),record.Logical.Length);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(row+12),record.Stored.Length);record.Stored.CopyTo(bytes,offset);offset+=record.Stored.Length;
        }
        return ImmutableArray.Create(bytes);
    }
}

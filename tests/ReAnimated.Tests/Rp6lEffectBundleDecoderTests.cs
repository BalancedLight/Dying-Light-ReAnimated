using System.Text;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Tests;

public sealed class Rp6lEffectBundleDecoderTests
{
    [Fact]
    public void ReadsExactSourceOffsetsUnknownKindsAndPreservesText()
    {
        byte[] bytes=Bundle(("generic_sequence",6,"SequenceDef() { Unknown(7) }\r\n"),("future_effect",-4,"UnknownDef()\n"));
        var rows=Rp6lEffectBundleDecoder.Decode(bytes);
        Assert.Equal(2,rows.Length);
        Assert.Equal("generic_sequence",rows[0].Name);
        Assert.Equal(6,rows[0].Kind);
        Assert.Equal(-4,rows[1].Kind);
        Assert.Equal("SequenceDef() { Unknown(7) }\r\n",rows[0].SourceText);
        Assert.Equal(Encoding.UTF8.GetBytes(rows[0].SourceText),bytes.AsSpan(rows[0].TextOffset,rows[0].TextByteLength).ToArray());
        Assert.Equal(rows[0].EntryByteLength,rows[1].EntryOffset);
        Assert.Equal(bytes.Length-1,rows[1].EntryOffset+rows[1].EntryByteLength);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rows[0].SourceText))),rows[0].ContentSha256);
    }
    [Fact]
    public void EveryTruncationIsRejectedWithoutPartialResults()
    {
        byte[] bytes=Bundle(("generic",6,"SequenceDef() {}"));
        for(int length=0;length<bytes.Length;length++)
            Assert.Throws<InvalidDataException>(()=>Rp6lEffectBundleDecoder.Decode(bytes.AsSpan(0,length)));
        Assert.Throws<InvalidDataException>(()=>Rp6lEffectBundleDecoder.Decode(bytes.Concat(new byte[]{0}).ToArray()));
        Assert.Throws<InvalidDataException>(()=>Rp6lEffectBundleDecoder.Decode(new byte[]{0xff,0,6,0,0}));
    }
    [Theory]
    [InlineData("../effect")]
    [InlineData("data//effect")]
    [InlineData("/effect")]
    [InlineData("effect:stream")]
    public void UnsafeNamesAreRejected(string name)=>Assert.Throws<InvalidDataException>(()=>Rp6lEffectBundleDecoder.Decode(Bundle((name,6,"Def()"))));
    [Fact]
    public void DuplicateCaseNamesAndCancellationAreRejected()
    {
        Assert.Throws<InvalidDataException>(()=>Rp6lEffectBundleDecoder.Decode(Bundle(("effect",6,"Def()"),("EFFECT",2,"Other()"))));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        Assert.Throws<OperationCanceledException>(()=>Rp6lEffectBundleDecoder.Decode(Bundle(("effect",6,"Def()")),canceled.Token));
        Assert.Empty(Rp6lEffectBundleDecoder.Decode(new byte[]{0}));
    }
    private static byte[] Bundle(params (string Name,sbyte Kind,string Text)[] rows)
    {
        using var output=new MemoryStream();
        foreach(var row in rows){output.Write(Encoding.UTF8.GetBytes(row.Name));output.WriteByte(0);output.WriteByte(unchecked((byte)row.Kind));output.Write(Encoding.UTF8.GetBytes(row.Text));output.WriteByte(0);}
        output.WriteByte(0);return output.ToArray();
    }
}
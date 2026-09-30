using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Tests;

public sealed class Dl1ProjectResourceCopiesTests : IDisposable
{
    private readonly string _root=RpackTestData.CreateTemporaryDirectory();
    private static Dl1DeveloperToolsDeploymentReceipt Receipt()=>new()
    {
        DeploymentId="0123456789abcdef01234567",CharacterId="generic",ModelResourceName="generic_model",AnimationLibraryName="generic_bank",
        AnimationScriptRelativePath="data/characters/animations/animscripts/generic_bank.scr",ModelCompilerFingerprint=new string('a',64),
        AnimationCompilerFingerprint=new string('b',64),CompletedUtc=DateTimeOffset.UnixEpoch,
    };
    private async Task<string> Pack(string relative,string resource,short type)
    {
        string path=Path.Combine(_root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path,RpackTestData.BuildArchive(resource,type,[new(1,"synthetic resource bytes"u8.ToArray())],RpackTestCompression.Zlib));
        return path;
    }

    [Fact]
    public async Task FindsEmbeddedNameAndTypeRegardlessOfPackFilename()
    {
        string matching=await Pack("data/maps/old_level.rpack","GENERIC_MODEL",Rp6lResourceTypes.Mesh);
        await Pack("assets_pc/irrelevant.rpack","generic_model",Rp6lResourceTypes.Animation);
        await Pack("root_bank.rpack","generic_bank",Rp6lResourceTypes.AnimationScript);
        var report=await Dl1ProjectResourceCopies.InspectAsync(_root,Receipt());
        Assert.True(report.Complete);Assert.Equal(3,report.ArchivesInspected);Assert.Equal(2,report.Copies.Length);
        var copy=Assert.Single(report.Copies,c=>c.ResourceType==Rp6lResourceTypes.Mesh);
        Assert.Equal("data/maps/old_level.rpack",copy.RelativePath);Assert.False(copy.RecordedByReceipt);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(matching))),copy.ContainerSha256);
        Assert.False(report.ActiveProviderVerified);
    }

    [Fact]
    public async Task RetainedReceiptPackIsDistinguishedAndHistoryIsOutsideScope()
    {
        const string path="out/ReAnimated/generic/model/generic_pc.rpack";
        string physical=await Pack(path,"generic_model",Rp6lResourceTypes.Mesh);
        await Pack(".dl-reanimated/backups/old.rpack","generic_model",Rp6lResourceTypes.Mesh);
        var receipt=Receipt() with{Artifacts=[new(path,Dl1DeploymentArtifactRole.PortableOnly,
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(physical))),null,null,true,true)]};
        var result=await Dl1ProjectResourceCopies.InspectAsync(_root,receipt);
        Assert.True(result.Complete);Assert.Equal(1,result.ArchivesInspected);
        Assert.Equal(Dl1DeploymentArtifactRole.PortableOnly,Assert.Single(result.Copies).ReceiptRole);
    }

    [Fact]
    public async Task BrokenArchiveMakesCoverageIncompleteWithoutDroppingGoodFindings()
    {
        await Pack("data/good.rpack","generic_model",Rp6lResourceTypes.Mesh);
        await File.WriteAllTextAsync(Path.Combine(_root,"data","broken.rpack"),"bad");
        var result=await Dl1ProjectResourceCopies.InspectAsync(_root,Receipt());
        Assert.False(result.Complete);Assert.Single(result.Copies);Assert.Contains(result.Diagnostics,d=>d.Contains("broken.rpack",StringComparison.Ordinal));
    }

    [Fact]
    public async Task HashBudgetRetainsNameEvidenceWithoutInventingDigest()
    {
        await Pack("data/control.rpack","generic_model",Rp6lResourceTypes.Mesh);
        var result=await Dl1ProjectResourceCopies.InspectAsync(_root,Receipt(),new(){MaximumTotalHashBytes=1});
        Assert.False(result.Complete);Assert.Null(Assert.Single(result.Copies).ContainerSha256);Assert.Equal(0,result.BytesHashed);
    }

    [Fact]
    public async Task ArchiveAndTableLimitsStayExplicit()
    {
        await Pack("data/a.rpack","generic_model",Rp6lResourceTypes.Mesh);
        await Pack("data/b.rpack","generic_model",Rp6lResourceTypes.Mesh);
        var limited=await Dl1ProjectResourceCopies.InspectAsync(_root,Receipt(),new(){MaximumArchives=1});
        Assert.False(limited.Complete);Assert.Equal(1,limited.ArchivesInspected);
        var tableLimited=await Dl1ProjectResourceCopies.InspectAsync(_root,Receipt(),new(){MaximumTotalTableBytes=1});
        Assert.False(tableLimited.Complete);Assert.Empty(tableLimited.Copies);
        Assert.Throws<ArgumentException>(()=>Dl1ProjectResourceCopies.InspectAsync(_root,Receipt(),new(){MaximumFiles=0}).GetAwaiter().GetResult());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Dl1ProjectResourceCopies.InspectAsync(_root,Receipt(),cancellationToken:new(true)));
    }

    [Fact]
    public async Task TextureAndAnimationIdentitiesComeFromReceiptInventory()
    {
        await Pack("assets_pc/renamed.rpack","generic_texture",Rp6lResourceTypes.Texture);
        var receipt=Receipt() with{Artifacts=[new("assets_pc/characters/generic/generic_texture.dds_obj",Dl1DeploymentArtifactRole.Compiled,new string('a',64),null,null,true,true)]};
        var result=await Dl1ProjectResourceCopies.InspectAsync(_root,receipt);
        Assert.Equal(Rp6lResourceTypes.Texture,Assert.Single(result.Copies).ResourceType);
    }

    [Fact]
    public async Task MalformedArchivesAlsoConsumeTheAttemptBudget()
    {
        Directory.CreateDirectory(Path.Combine(_root,"data"));
        for(int i=0;i<4;i++)await File.WriteAllTextAsync(Path.Combine(_root,"data",$"bad{i}.rpack"),"broken");
        var result=await Dl1ProjectResourceCopies.InspectAsync(_root,Receipt(),new(){MaximumArchives=2,MaximumDiagnostics=1});
        Assert.False(result.Complete);Assert.Equal(2,result.ArchivesInspected);Assert.Equal(2,result.Diagnostics.Length);
        Assert.Contains(result.Diagnostics,d=>d.Contains("omitted",StringComparison.Ordinal));
    }

    public void Dispose()=>RpackTestData.DeleteTemporaryDirectory(_root);
}

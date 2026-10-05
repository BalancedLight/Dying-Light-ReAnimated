using System.Globalization;
using System.Text.Json;
using ReAnimated.Cli;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

[Collection("Character CLI")]
public sealed class CharacterBoundsCommandTests
{
    [Fact]
    public async Task FittedBoundsAreReportedAsCandidateWithoutSavingOrEditingInput()
    {
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            string input=Path.Combine(directory,"character.dlrmodel");var package=Package();CustomModelPackageSerializer.SaveAtomic(package,input);
            byte[] before=await File.ReadAllBytesAsync(input);var model=ReAnimated.Codecs.Fbx.FbxModelAuthoringImporter.ImportPackage(package);
            int boneIndex=Assert.Single(model.Surfaces).PaletteBoneIndices[0];string bone=package.Document.CreateEffectiveBones()[boneIndex].Name;
            var result=await Run(["character","fit-bounds",input,"--bone",bone]);Assert.True(result.Code==0,result.Output);
            using var json=JsonDocument.Parse(result.Output);Assert.Equal("meters",json.RootElement.GetProperty("units").GetString());
            Assert.Equal(3,json.RootElement.GetProperty("size").GetArrayLength());Assert.Equal(before,await File.ReadAllBytesAsync(input));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }

    [Fact]
    public async Task ReviewedBoundsEditSurvivesReloadWithoutChangingInputOrGeometry()
    {
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            var package=Package();string input=Path.Combine(directory,"character.dlrmodel"),output=Path.Combine(directory,"edited.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(package,input);byte[] before=await File.ReadAllBytesAsync(input);string bone=package.Document.Bones[0].Name;
            string[] args=["character","set-bounds",input,"--bone",bone,"--center","1,2,3","--size","2,4,6","--reviewed","--output",output];
            var result=await Run(args);Assert.True(result.Code==0,result.Output);
            var reopened=CustomModelPackageSerializer.Load(output);var bounds=reopened.Document.Bones[0].LocalBounds!.Value;
            Assert.Equal(new Vector3D(1,2,3),bounds.Center);Assert.Equal(new Vector3D(1,2,3),bounds.HalfExtents);
            Assert.Equal(package.SourceFbx.ToArray(),reopened.SourceFbx.ToArray());Assert.Equal(before,await File.ReadAllBytesAsync(input));
            Assert.Null(reopened.Document.LastBuildReceipt);Assert.Null(reopened.Document.CharacterResources!.LoadedResourceSha256);
            Assert.Equal(2,(await Run(args)).Code);
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }
    [Fact]
    public async Task AxisControlBatchContainsBaselineAndThreeIndependentDimensionEdits()
    {
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            var package=Package();string input=Path.Combine(directory,"character.dlrmodel"),output=Path.Combine(directory,"controls");CustomModelPackageSerializer.SaveAtomic(package,input);
            string bone=package.Document.Bones[0].Name;var result=await Run(["character","bounds-controls",input,"--bone",bone,"--scale","1.5","--output-dir",output]);Assert.True(result.Code==0,result.Output);
            using var json=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output,"controls.json")));var rows=json.RootElement.GetProperty("controls");Assert.Equal(4,rows.GetArrayLength());
            var baseline=package.Document.Bones[0].LocalBounds!.Value;
            foreach(var row in rows.EnumerateArray())
            {
                var saved=CustomModelPackageSerializer.Load(Path.Combine(output,row.GetProperty("fileName").GetString()!));var current=saved.Document.Bones[0].LocalBounds!.Value;
                Assert.Equal(baseline.Center,current.Center);Assert.Equal(package.SourceFbx.ToArray(),saved.SourceFbx.ToArray());
                string? axis=row.GetProperty("axis").GetString();
                Assert.Equal(axis=="X"?baseline.HalfExtents.X*1.5:baseline.HalfExtents.X,current.HalfExtents.X);
                Assert.Equal(axis=="Y"?baseline.HalfExtents.Y*1.5:baseline.HalfExtents.Y,current.HalfExtents.Y);
                Assert.Equal(axis=="Z"?baseline.HalfExtents.Z*1.5:baseline.HalfExtents.Z,current.HalfExtents.Z);
            }
            Assert.DoesNotContain(directory,result.Output,StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2,(await Run(["character","bounds-controls",input,"--bone",bone,"--scale","1.5","--output-dir",output])).Code);
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }
    [Fact]
    public async Task InvalidBoundsAndMissingReviewProduceNoOutputs()
    {
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            string input=Path.Combine(directory,"character.dlrmodel"),output=Path.Combine(directory,"edited.dlrmodel");var package=Package();CustomModelPackageSerializer.SaveAtomic(package,input);
            string[] args=["character","set-bounds",input,"--bone",package.Document.Bones[0].Name,"--center","0,0,0","--size","-1,2,3","--output",output];
            Assert.Equal(2,(await Run(args)).Code);Assert.Equal(2,(await Run([..args,"--reviewed"])).Code);Assert.False(File.Exists(output));
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            Assert.Equal(130,(await Run(["character","bounds-controls",input,"--bone",package.Document.Bones[0].Name,"--scale","1.5","--output-dir",Path.Combine(directory,"controls")],cancel.Token)).Code);
            Assert.False(Directory.Exists(Path.Combine(directory,"controls")));Assert.Empty(Directory.GetDirectories(directory,"*.tmp"));
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }
    private static CustomModelPackage Package()
    {
        var package=ModelsWorkspaceMorphAuthoringTests.CreateGenericDifferentTopologyTargetPackage();
        var model=ReAnimated.Codecs.Fbx.FbxModelAuthoringImporter.ImportPackage(package);
        return ReAnimated.Codecs.Models.CharacterBoneBoundsAuthoring.Apply(model,package.Document.Bones[0].Name,
            new(0.1,0.2,0.3),new(2,4,6),reviewed:true).Package;
    }
    private static async Task<(int Code,string Output)> Run(string[] args,CancellationToken token=default)
    {
        TextWriter oldOut=Console.Out,oldError=Console.Error;using var output=new StringWriter(CultureInfo.InvariantCulture);using var errors=new StringWriter(CultureInfo.InvariantCulture);
        try{Console.SetOut(output);Console.SetError(errors);return(await CliApplication.RunAsync(args,token),output.ToString()+errors.ToString());}
        finally{Console.SetOut(oldOut);Console.SetError(oldError);}
    }
}

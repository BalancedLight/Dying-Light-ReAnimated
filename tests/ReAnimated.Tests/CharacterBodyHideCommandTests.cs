using System.Globalization;
using System.Text.Json;
using ReAnimated.Cli;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

[Collection("Character CLI")]
public sealed class CharacterBodyHideCommandTests
{
    [Fact]
    public async Task ReviewedRegionAdditionRetainsInputAndOriginalSourceArchive()
    {
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            var package=ModelsWorkspaceCharacterRelationshipTests.CreatePackage();
            string source=Path.Combine(directory,"character.dlrmodel"),output=Path.Combine(directory,"authored.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(package,source);byte[] before=await File.ReadAllBytesAsync(source);
            string entity=package.Document.CreateEffectiveBones()[0].Name;
            string[] args=["character","body-hide",source,"--resource","body","--region","_A","--entity",entity,"--reviewed","--output",output];
            var result=await Run(args);Assert.True(result.Code==0,result.Output);
            using var report=JsonDocument.Parse(result.Output);Assert.Equal("dl-reanimated-body-hide-addition-v1",report.RootElement.GetProperty("format").GetString());
            var saved=CustomModelPackageSerializer.Load(output);
            var read=CharacterCompanionAuthoring.Read(saved,"body",CharacterCompanionFamily.BodyElements);
            var selected=read.BodyElements!.Elements.Single(element=>element.ElementToken=="_A");
            Assert.Contains(read.BodyElements.MeshDisables,disable=>disable.BodyElementCallIndex==selected.CallIndex&&!disable.FromRelic&&disable.EntityName==entity);
            var archive=saved.Document.CharacterResources!.Resources.Single(resource=>resource.Id=="original:body");
            var old=package.Document.CharacterResources!.Resources.Single(resource=>resource.Id=="body");
            Assert.Equal(package.CompanionPayloads[old.EntryPath!].ToArray(),saved.CompanionPayloads[archive.EntryPath!].ToArray());
            Assert.Equal(before,await File.ReadAllBytesAsync(source));
            Assert.Equal(2,(await Run(args)).Code);
            Assert.DoesNotContain(directory,result.Output,StringComparison.OrdinalIgnoreCase);
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }

    [Fact]
    public async Task InvalidReviewRegionOrEntityProducesNoOutput()
    {
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            var package=ModelsWorkspaceCharacterRelationshipTests.CreatePackage();
            string source=Path.Combine(directory,"character.dlrmodel"),output=Path.Combine(directory,"authored.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(package,source);
            string[] args=["character","body-hide",source,"--resource","body","--region","_A","--entity",package.Document.CreateEffectiveBones()[0].Name,"--output",output];
            Assert.Equal(2,(await Run(args)).Code);
            Assert.Equal(2,(await Run(["character","body-hide",source,"--resource","body","--region","_UNKNOWN","--entity",package.Document.CreateEffectiveBones()[0].Name,"--reviewed","--output",output])).Code);
            Assert.Equal(2,(await Run(["character","body-hide",source,"--resource","body","--region","_A","--entity","missing_entity","--reviewed","--output",output])).Code);
            Assert.False(File.Exists(output));
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }
    private static async Task<(int Code,string Output)> Run(string[] args)
    {
        TextWriter oldOut=Console.Out,oldError=Console.Error;using var output=new StringWriter(CultureInfo.InvariantCulture);using var error=new StringWriter(CultureInfo.InvariantCulture);
        try{Console.SetOut(output);Console.SetError(error);int code=await CliApplication.RunAsync(args);return(code,output.ToString()+error.ToString());}
        finally{Console.SetOut(oldOut);Console.SetError(oldError);}
    }
}

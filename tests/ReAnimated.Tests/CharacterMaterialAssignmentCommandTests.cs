using System.Globalization;
using System.Text.Json;
using ReAnimated.Cli;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

[Collection("Character CLI")]
public sealed class CharacterMaterialAssignmentCommandTests
{
    [Fact]
    public async Task ReviewedAssignmentUsesExactGuidsAndRetainsOriginalSlots()
    {
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            var model=CharacterAccessoryMaterialAuthoringTests.Create();
            string source=Path.Combine(directory,"character.dlrmodel"),output=Path.Combine(directory,"authored.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(model.Package,source);
            byte[] before=await File.ReadAllBytesAsync(source);
            Guid appended=model.Package.Document.Materials[^1].Id,retained=model.Package.Document.Materials[0].Id;
            string[] args=["character","assign-material",source,"--target-material",appended.ToString(),"--use-material",retained.ToString(),"--reviewed","--output",output];
            var result=await Run(args);Assert.True(result.Code==0,result.Output);
            using var report=JsonDocument.Parse(result.Output);
            Assert.Equal("dl-reanimated-accessory-material-assignment-v1",report.RootElement.GetProperty("format").GetString());
            var saved=FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(output));
            Assert.DoesNotContain(saved.Package.Document.Materials,material=>material.Id==appended);
            Assert.All(saved.Surfaces.Where(surface=>surface.Id.StartsWith("attachment:",StringComparison.Ordinal)),surface=>Assert.Equal(retained,surface.MaterialId));
            Assert.Equal(before,await File.ReadAllBytesAsync(source));
            Assert.Equal(2,(await Run(args)).Code);
            var inspection=await Run(["character","inspect",output]);
            using var inspected=JsonDocument.Parse(inspection.Output);
            Assert.True(inspected.RootElement.GetProperty("materials").GetArrayLength()>0);
            Assert.All(inspected.RootElement.GetProperty("surfaces").EnumerateArray(),surface=>Assert.True(surface.TryGetProperty("materialId",out _)));
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }

    [Fact]
    public async Task MissingReviewOrInvalidIdentifierProducesNoOutput()
    {
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            var model=CharacterAccessoryMaterialAuthoringTests.Create();
            string source=Path.Combine(directory,"character.dlrmodel"),output=Path.Combine(directory,"authored.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(model.Package,source);
            string[] args=["character","assign-material",source,"--target-material",model.Package.Document.Materials[^1].Id.ToString(),"--use-material",model.Package.Document.Materials[0].Id.ToString(),"--output",output];
            Assert.Equal(2,(await Run(args)).Code);
            Assert.Equal(2,(await Run(["character","assign-material",source,"--target-material","invalid","--use-material",model.Package.Document.Materials[0].Id.ToString(),"--reviewed","--output",output])).Code);
            Assert.False(File.Exists(output));
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }

    private static async Task<(int Code,string Output)> Run(string[] args)
    {
        TextWriter oldOut=Console.Out,oldError=Console.Error;
        using var output=new StringWriter(CultureInfo.InvariantCulture);using var error=new StringWriter(CultureInfo.InvariantCulture);
        try{Console.SetOut(output);Console.SetError(error);int code=await CliApplication.RunAsync(args);return(code,output.ToString()+error.ToString());}
        finally{Console.SetOut(oldOut);Console.SetError(oldError);}
    }
}

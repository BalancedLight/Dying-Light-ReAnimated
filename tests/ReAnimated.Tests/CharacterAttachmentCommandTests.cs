using System.Globalization;
using System.Text.Json;
using ReAnimated.Cli;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

[Collection("Character CLI")]
public sealed class CharacterAttachmentCommandTests
{
    [Fact]
    public async Task RigidAccessorySaveRetainsOriginalFacesMorphsRigAndCompanionBytes()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var package = ModelsWorkspaceMorphAuthoringTests.CreateGenericReferencePackage();
            string input = Path.Combine(directory, "character.dlrmodel"),
                attachment = Path.Combine(directory, "accessory.fbx"), output = Path.Combine(directory, "authored.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(package, input);
            await File.WriteAllBytesAsync(attachment, ModelsWorkspaceMorphAuthoringTests.CreateGenericManualSculptFbx(0));
            var original = FbxModelAuthoringImporter.ImportPackage(package);
            string bone = package.Document.CreateEffectiveBones()[0].Name;
            byte[] before = await File.ReadAllBytesAsync(input);
            string[] args = ["character", "add-attachment", input, "--attachment", attachment,
                "--bone", bone, "--reviewed", "--output", output];
            var result = await Run(args);
            Assert.True(result.Code == 0, result.Output);
            var saved = CustomModelPackageSerializer.Load(output);
            var reopened = FbxModelAuthoringImporter.ImportPackage(saved);
            Assert.Equal(original.Surfaces.Length + 1, reopened.Surfaces.Length);
            Assert.Equal(JsonSerializer.Serialize(original.Surfaces),
                JsonSerializer.Serialize(reopened.Surfaces.Take(original.Surfaces.Length).ToArray()));
            Assert.Equal(JsonSerializer.Serialize(package.Document.MorphChannels), JsonSerializer.Serialize(saved.Document.MorphChannels));
            Assert.Equal(JsonSerializer.Serialize(package.Document.Bones), JsonSerializer.Serialize(saved.Document.Bones));
            Assert.Equal(package.SourceFbx.ToArray(), saved.SourceFbx.ToArray());
            Assert.Equal(package.CompanionPayloads.Count, saved.CompanionPayloads.Count);
            foreach (var pair in package.CompanionPayloads)
                Assert.Equal(pair.Value.ToArray(), saved.CompanionPayloads[pair.Key].ToArray());
            int targetBone = Array.FindIndex(saved.Document.CreateEffectiveBones().ToArray(), value => value.Name == bone);
            var added = reopened.Surfaces[^1];
            Assert.Equal(new[] { targetBone }, added.PaletteBoneIndices.ToArray());
            Assert.All(added.Vertices, vertex => { Assert.Equal(0, Assert.Single(vertex.BoneIndices)); Assert.Equal(1d, Assert.Single(vertex.BoneWeights)); });
            Assert.Equal(before, await File.ReadAllBytesAsync(input));
            Assert.Null(saved.Document.LastBuildReceipt);
            Assert.Null(saved.Document.CharacterResources!.CompiledSemanticSha256);
            Assert.Null(saved.Document.CharacterResources.LoadedResourceSha256);
            using var report = JsonDocument.Parse(result.Output);
            Assert.Equal("dl-reanimated-character-attachment-v1", report.RootElement.GetProperty("format").GetString());
            Assert.DoesNotContain(directory, result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, (await Run(args)).Code);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task MissingReviewAndUnknownBoneProduceNoOutput()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string input = Path.Combine(directory, "character.dlrmodel"), attachment = Path.Combine(directory, "accessory.fbx"),
                output = Path.Combine(directory, "authored.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(ModelsWorkspaceMorphAuthoringTests.CreateGenericReferencePackage(), input);
            await File.WriteAllBytesAsync(attachment, ModelsWorkspaceMorphAuthoringTests.CreateGenericManualSculptFbx(0));
            string[] args = ["character", "add-attachment", input, "--attachment", attachment, "--output", output];
            Assert.Equal(2, (await Run(args)).Code);
            Assert.Equal(2, (await Run([.. args, "--reviewed", "--bone", "missing_bone"])).Code);
            Assert.False(File.Exists(output));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Assert.Equal(130, (await Run([.. args, "--reviewed"], cancel.Token)).Code);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static async Task<(int Code, string Output)> Run(string[] args, CancellationToken token = default)
    {
        TextWriter beforeOut = Console.Out, beforeError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output); Console.SetError(error);
            int code = await CliApplication.RunAsync(args, token);
            return (code, output.ToString() + error.ToString());
        }
        finally { Console.SetOut(beforeOut); Console.SetError(beforeError); }
    }
}

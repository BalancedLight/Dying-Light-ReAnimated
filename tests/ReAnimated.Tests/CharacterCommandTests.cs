using System.Globalization;
using System.Text.Json;
using ReAnimated.Cli;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

[CollectionDefinition("Character CLI", DisableParallelization = true)]
public sealed class CharacterCliTestGroup { }

[Collection("Character CLI")]
public sealed class CharacterCommandTests
{
    [Fact]
    public async Task InspectReportsPortableMetadataAndOriginalMorphBindingsWithoutGeometryPayloads()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            CustomModelPackage package = ModelsWorkspaceMorphAuthoringTests.CreateGenericReferencePackage();
            string source = Path.Combine(directory, "reference.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(package, source);
            byte[] before = await File.ReadAllBytesAsync(source);

            var result = await RunAsync(["character", "inspect", source]);

            Assert.Equal(0, result.ExitCode);
            using JsonDocument report = JsonDocument.Parse(result.Output);
            JsonElement root = report.RootElement;
            Assert.Equal("dl-reanimated-character-inspection-v1", root.GetProperty("format").GetString());
            Assert.Equal(package.Document.Source.ContentSha256, root.GetProperty("sourceSha256").GetString());
            Assert.Single(root.GetProperty("surfaces").EnumerateArray());
            Assert.Equal(package.Document.MorphChannels.Length, root.GetProperty("morphs").GetArrayLength());
            CharacterMorphBinding binding = package.Document.CharacterResources!.MorphBindings[0];
            JsonElement reported = root.GetProperty("lodBindings")[0];
            Assert.Equal(binding.SourceChannelIndex, reported.GetProperty("sourceChannelIndex").GetInt32());
            Assert.Equal(binding.TargetChannelSlot, reported.GetProperty("targetChannelSlot").GetInt32());
            Assert.Equal(binding.Name, reported.GetProperty("name").GetString());
            Assert.Equal(binding.DescriptorHash, reported.GetProperty("descriptorHash").GetUInt32());
            Assert.Equal(binding.LodIndex, reported.GetProperty("lodIndex").GetInt32());
            Assert.Equal(package.Document.CreateEffectiveBones().Length, root.GetProperty("bounds").GetArrayLength());
            Assert.Equal(package.Document.CharacterResources.Subsystems.Length, root.GetProperty("subsystems").GetArrayLength());
            Assert.Equal(package.Document.CharacterResources.Resources.Length, root.GetProperty("resources").GetArrayLength());
            Assert.NotEmpty(root.GetProperty("blockers").EnumerateArray());
            Assert.Empty(root.GetProperty("reviews").GetProperty("expressions").EnumerateArray());
            Assert.DoesNotContain("\"vertices\"", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("\"positionDeltas\"", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("\"sourceFbx\"", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("\"decodedCharacterPayload\"", result.Output, StringComparison.Ordinal);
            Assert.Equal(before, await File.ReadAllBytesAsync(source));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task ExportNeutralRetainsTopologyAndDoesNotOverwriteExistingOutput()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var package = ModelsWorkspaceMorphAuthoringTests.CreateGenericManualTargetPackage(
                ModelsWorkspaceMorphAuthoringTests.CreateGenericManualSculptFbx(0));
            string source = Path.Combine(directory, "target.dlrmodel");
            string output = Path.Combine(directory, "face.obj");
            CustomModelPackageSerializer.SaveAtomic(package, source);
            byte[] before = await File.ReadAllBytesAsync(source);
            var model = FbxModelAuthoringImporter.ImportPackage(package);
            var surface = Assert.Single(model.Surfaces);
            string[] args = ["character", "export-neutral", source, "--surface", surface.Id, "--output", output];

            Assert.Equal(0, (await RunAsync(args)).ExitCode);
            byte[] exported = await File.ReadAllBytesAsync(output);
            Assert.All(ManualMorphObjCodec.ComputePositionDeltas(surface, exported),
                delta => Assert.Equal(Vector3D.Zero, delta));
            Assert.Equal(2, (await RunAsync(args)).ExitCode);
            Assert.Equal(exported, await File.ReadAllBytesAsync(output));
            Assert.Equal(before, await File.ReadAllBytesAsync(source));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData(".obj")]
    [InlineData(".fbx")]
    public async Task ImportSculptPersistsExpressionReviewAndNeutralGeometry(string extension)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var inputs = await CreateInputsAsync(directory, extension);
            string output = Path.Combine(directory, "authored.dlrmodel");
            byte[] targetBytes = await File.ReadAllBytesAsync(inputs.Target);
            byte[] referenceBytes = await File.ReadAllBytesAsync(inputs.Reference);
            byte[] sculptBytes = await File.ReadAllBytesAsync(inputs.Sculpt);
            var original = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(inputs.Target));
            var reference = CustomModelPackageSerializer.Load(inputs.Reference);
            var referenceBinding = reference.Document.CharacterResources!.MorphBindings.Single(binding => binding.Name == inputs.Expression);

            Assert.Equal(0, (await RunAsync(ImportArgs(inputs, inputs.Target, output))).ExitCode);

            var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(output));
            var surface = Assert.Single(reopened.Surfaces);
            var expression = Assert.Single(surface.MorphTargets);
            Assert.Equal(inputs.Expression, expression.Name);
            Assert.Equal(referenceBinding.DescriptorHash, expression.DescriptorHash);
            Assert.Contains(expression.PositionDeltas, delta => delta.LengthSquared > 0);
            Assert.Equal(original.Surfaces[0].Vertices.Select(vertex => vertex.Position),
                surface.Vertices.Select(vertex => vertex.Position));
            Assert.Equal(original.Surfaces[0].Indices.ToArray(), surface.Indices.ToArray());
            Assert.Equal(original.Package.SourceFbx.ToArray(), reopened.Package.SourceFbx.ToArray());
            Assert.Equal(original.Package.Document.Bones.ToArray(), reopened.Package.Document.Bones.ToArray());
            var review = Assert.Single(reopened.Package.Document.MorphAuthoringRecords);
            Assert.True(review.Accepted);
            Assert.Equal(MorphAuthoringMethod.ManualSculpt, review.Method);
            Assert.Equal(referenceBinding.SourceChannelIndex, review.OriginalSourceChannelIndex);
            Assert.Equal(referenceBinding.DescriptorHash, review.DescriptorHash);
            Assert.Equal(reference.Document.Source.ContentSha256, review.ReferenceSourceSha256);
            Assert.Equal(MorphAuthoringEvidence.ExpressionFingerprint(expression), review.AuthoredExpressionSha256);
            Assert.Empty(MorphAuthoringEvidence.ExportBlockers(reopened));
            Assert.Null(reopened.Package.Document.CharacterResources!.CompiledSemanticSha256);
            Assert.Null(reopened.Package.Document.CharacterResources.LoadedResourceSha256);
            Assert.Empty(reopened.Package.Document.CharacterResources.VerifiedPlayerScenarios);
            Assert.Equal(targetBytes, await File.ReadAllBytesAsync(inputs.Target));
            Assert.Equal(referenceBytes, await File.ReadAllBytesAsync(inputs.Reference));
            Assert.Equal(sculptBytes, await File.ReadAllBytesAsync(inputs.Sculpt));
            byte[] created = await File.ReadAllBytesAsync(output);
            Assert.Equal(2, (await RunAsync(ImportArgs(inputs, inputs.Target, output))).ExitCode);
            Assert.Equal(created, await File.ReadAllBytesAsync(output));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task ConflictsRequireExplicitReplacementAndKeepRetainsExistingExpression()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var inputs = await CreateInputsAsync(directory, ".fbx");
            string first = Path.Combine(directory, "first.dlrmodel");
            Assert.Equal(0, (await RunAsync(ImportArgs(inputs, inputs.Target, first))).ExitCode);
            var accepted = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(first));
            var oldExpression = Assert.Single(accepted.Surfaces[0].MorphTargets);
            byte[] firstBytes = await File.ReadAllBytesAsync(first);
            await File.WriteAllBytesAsync(inputs.Sculpt, ModelsWorkspaceMorphAuthoringTests.CreateGenericManualSculptFbx(0.5));

            string rejected = Path.Combine(directory, "rejected.dlrmodel");
            Assert.Equal(2, (await RunAsync(ImportArgs(inputs, first, rejected))).ExitCode);
            Assert.False(File.Exists(rejected));
            string kept = Path.Combine(directory, "kept.dlrmodel");
            Assert.Equal(0, (await RunAsync(ImportArgs(inputs, first, kept, "keep"))).ExitCode);
            var keptModel = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(kept));
            Assert.Equal(oldExpression.PositionDeltas.ToArray(), Assert.Single(keptModel.Surfaces[0].MorphTargets).PositionDeltas.ToArray());
            Assert.Equal(JsonSerializer.Serialize(accepted.Package.Document.MorphAuthoringRecords),
                JsonSerializer.Serialize(keptModel.Package.Document.MorphAuthoringRecords));
            string replaced = Path.Combine(directory, "replaced.dlrmodel");
            Assert.Equal(0, (await RunAsync(ImportArgs(inputs, first, replaced, "replace"))).ExitCode);
            var replacement = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(replaced));
            Assert.False(oldExpression.PositionDeltas.SequenceEqual(Assert.Single(replacement.Surfaces[0].MorphTargets).PositionDeltas));
            Assert.True(Assert.Single(replacement.Package.Document.MorphAuthoringRecords).Accepted);
            Assert.Equal(MorphTransferConflict.ReplaceExisting,
                Assert.Single(replacement.Package.Document.MorphAuthoringRecords).ConflictChoice);
            Assert.Equal(firstBytes, await File.ReadAllBytesAsync(first));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing-value")]
    [InlineData("missing-review")]
    [InlineData("source-output")]
    [InlineData("reference-output")]
    [InlineData("bad-conflict")]
    [InlineData("missing-surface")]
    [InlineData("changed-topology")]
    public async Task InvalidInputsCreateNoOutputAndRetainInputs(string scenario)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var inputs = await CreateInputsAsync(directory, ".obj");
            string output = Path.Combine(directory, "rejected.dlrmodel");
            var args = ImportArgs(inputs, inputs.Target, output).ToList();
            switch (scenario)
            {
                case "unknown": args.AddRange(["--unexpected", "value"]); break;
                case "duplicate": args.AddRange(["--output", output]); break;
                case "missing-value": args.Add("--conflict"); break;
                case "missing-review": args.Remove("--reviewed"); break;
                case "source-output": args[^1] = inputs.Target; break;
                case "reference-output": args[^1] = inputs.Reference; break;
                case "bad-conflict": args.AddRange(["--conflict", "merge"]); break;
                case "missing-surface": args[args.IndexOf("--target-surface") + 1] = "absent-surface"; break;
                case "changed-topology": await File.AppendAllTextAsync(inputs.Sculpt, "f 1 2 3\n"); break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            byte[] targetBefore = await File.ReadAllBytesAsync(inputs.Target);
            byte[] referenceBefore = await File.ReadAllBytesAsync(inputs.Reference);
            byte[] sculptBefore = await File.ReadAllBytesAsync(inputs.Sculpt);

            var result = await RunAsync(args.ToArray());

            Assert.Equal(2, result.ExitCode);
            Assert.NotEmpty(result.Error);
            Assert.False(File.Exists(output));
            Assert.Equal(targetBefore, await File.ReadAllBytesAsync(inputs.Target));
            Assert.Equal(referenceBefore, await File.ReadAllBytesAsync(inputs.Reference));
            Assert.Equal(sculptBefore, await File.ReadAllBytesAsync(inputs.Sculpt));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task CancellationReturns130BeforeCreatingOutput()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            string output = Path.Combine(directory, "face.obj");
            var result = await RunAsync(["character", "export-neutral", Path.Combine(directory, "missing.dlrmodel"),
                "--surface", "surface/0", "--output", output], cancellation.Token);
            Assert.Equal(130, result.ExitCode);
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static async Task<CharacterInputs> CreateInputsAsync(string directory, string sculptExtension)
    {
        string target = Path.Combine(directory, "target.dlrmodel");
        string reference = Path.Combine(directory, "reference.dlrmodel");
        string sculpt = Path.Combine(directory, "sculpt" + sculptExtension);
        var targetPackage = ModelsWorkspaceMorphAuthoringTests.CreateGenericManualTargetPackage(
            ModelsWorkspaceMorphAuthoringTests.CreateGenericManualSculptFbx(0));
        var referencePackage = ModelsWorkspaceMorphAuthoringTests.CreateGenericReferencePackage();
        CustomModelPackageSerializer.SaveAtomic(targetPackage, target);
        CustomModelPackageSerializer.SaveAtomic(referencePackage, reference);
        byte[] sculptFbx = ModelsWorkspaceMorphAuthoringTests.CreateGenericManualSculptFbx(0.25);
        byte[] sculptBytes = sculptExtension == ".fbx" ? sculptFbx : ManualMorphObjCodec.EncodeNeutral(
            Assert.Single(FbxModelAuthoringImporter.Import(sculptFbx, "sculpt.fbx",
                new() { DecodeAnimationClips = false }).Surfaces));
        await File.WriteAllBytesAsync(sculpt, sculptBytes);
        return new(target, reference, sculpt,
            Assert.Single(FbxModelAuthoringImporter.ImportPackage(targetPackage).Surfaces).Id,
            Assert.Single(FbxModelAuthoringImporter.ImportPackage(referencePackage).Surfaces).Id,
            referencePackage.Document.MorphChannels[0].Name);
    }

    private static string[] ImportArgs(CharacterInputs inputs, string target, string output, string? conflict = null)
    {
        List<string> args = ["character", "import-sculpt", target, "--reference", inputs.Reference,
            "--target-surface", inputs.TargetSurface, "--reference-surface", inputs.ReferenceSurface,
            "--expression", inputs.Expression, "--sculpt", inputs.Sculpt];
        if (conflict is not null) args.AddRange(["--conflict", conflict]);
        args.AddRange(["--reviewed", "--output", output]);
        return args.ToArray();
    }

    private static async Task<CliResult> RunAsync(string[] args, CancellationToken token = default)
    {
        TextWriter previousOutput = Console.Out;
        TextWriter previousError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int exitCode = await CliApplication.RunAsync(args, token);
            return new(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
    }

    private sealed record CharacterInputs(string Target, string Reference, string Sculpt,
        string TargetSurface, string ReferenceSurface, string Expression);
    private sealed record CliResult(int ExitCode, string Output, string Error);
}
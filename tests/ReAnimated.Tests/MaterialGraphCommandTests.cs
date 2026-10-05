using System.Globalization;
using System.Text.Json;
using ReAnimated.Cli;
using ReAnimated.Codecs.Materials;

namespace ReAnimated.Tests;

[CollectionDefinition("Material Graph CLI", DisableParallelization = true)]
public sealed class MaterialGraphCliTestGroup { }

[Collection("Material Graph CLI")]
public sealed class MaterialGraphCommandTests
{
    [Fact]
    public async Task InspectReportsPortableHashesAndUnionCounts()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var destination = Dl1CompiledMaterialGraphMergerTests.Pack("future", (1, [1], [1, 7]));
            var source = Dl1CompiledMaterialGraphMergerTests.Pack("future", (1, [1], [1, 9]), (2, [2], [2, 8]));
            var paths = await WriteInputs(directory, destination.ToArray(), source.ToArray());
            var result = await Run(["material-graph", "inspect", "--destination", paths.Destination, "--source", paths.Source]);
            Assert.Equal(0, result.Code);
            Assert.Empty(result.Error);
            using var json = JsonDocument.Parse(result.Output);
            var root = json.RootElement;
            Assert.Equal("destination.mp", root.GetProperty("destination").GetProperty("fileName").GetString());
            Assert.Equal("source.mp", root.GetProperty("source").GetProperty("fileName").GetString());
            Assert.Equal(Dl1CompiledMaterialGraphMerger.Inspect(destination, source).DestinationSha256,
                root.GetProperty("destination").GetProperty("sha256").GetString());
            Assert.True(root.GetProperty("plan").GetProperty("canMerge").GetBoolean());
            Assert.Equal(1, root.GetProperty("plan").GetProperty("addedRecords").GetInt32());
            Assert.Equal(1, root.GetProperty("plan").GetProperty("identicalRecords").GetInt32());
            Assert.DoesNotContain(directory, result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(destination.ToArray(), await File.ReadAllBytesAsync(paths.Destination));
            Assert.Equal(source.ToArray(), await File.ReadAllBytesAsync(paths.Source));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("merge")]
    public async Task ConflictsReturnJsonAndExitTwoWithoutWriting(string command)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var paths = await WriteInputs(directory,
                Dl1CompiledMaterialGraphMergerTests.Pack("future", (9, [1, 2], [1, 2])).ToArray(),
                Dl1CompiledMaterialGraphMergerTests.Pack("future", (9, [1, 3], [1, 3])).ToArray());
            string output = Path.Combine(directory, "merged.mp");
            string[] args = command == "inspect"
                ? ["material-graph", command, "--destination", paths.Destination, "--source", paths.Source]
                : ["material-graph", command, "--destination", paths.Destination, "--source", paths.Source, "--output", output, "--reviewed"];
            var result = await Run(args);
            Assert.Equal(2, result.Code);
            Assert.Empty(result.Error);
            using var json = JsonDocument.Parse(result.Output);
            var plan = json.RootElement.GetProperty("plan");
            Assert.False(plan.GetProperty("canMerge").GetBoolean());
            Assert.Equal(1, plan.GetProperty("conflictCount").GetInt32());
            var conflict = Assert.Single(plan.GetProperty("conflicts").EnumerateArray());
            Assert.Equal("future", conflict.GetProperty("container").GetString());
            Assert.Equal("0x00000009", conflict.GetProperty("key").GetString());
            Assert.NotEqual(conflict.GetProperty("destinationSha256").GetString(), conflict.GetProperty("sourceSha256").GetString());
            Assert.False(File.Exists(output));
            Assert.DoesNotContain(directory, result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task ReviewedMergeWritesANewGraphAndRetainsBothInputs()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] destination = Dl1CompiledMaterialGraphMergerTests.Pack("future", (1, [1], [1, 7])).ToArray();
            byte[] source = Dl1CompiledMaterialGraphMergerTests.Pack("future", (2, [2], [2, 8])).ToArray();
            var paths = await WriteInputs(directory, destination, source);
            string output = Path.Combine(directory, "merged.mp");
            var result = await Run(["material-graph", "merge", "--destination", paths.Destination,
                "--source", paths.Source, "--output", output, "--reviewed"]);
            Assert.Equal(0, result.Code);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Equal("merged.mp", json.RootElement.GetProperty("output").GetProperty("fileName").GetString());
            var merged = Dl1CompiledMaterialGraphReader.Read(
                System.Collections.Immutable.ImmutableArray.Create(await File.ReadAllBytesAsync(output)));
            Assert.Equal(2, merged.TotalRecords);
            Assert.Equal(new byte[] { 1, 7 }, merged.Containers[0].Records[0].StoredBytes.ToArray());
            Assert.Equal(destination, await File.ReadAllBytesAsync(paths.Destination));
            Assert.Equal(source, await File.ReadAllBytesAsync(paths.Source));
            Assert.DoesNotContain(directory, result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task MissingReviewInputOutputsAndExistingOutputsAreRefused()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] graph = Dl1CompiledMaterialGraphMergerTests.Pack("future", (1, [1], [1])).ToArray();
            var paths = await WriteInputs(directory, graph, graph);
            string output = Path.Combine(directory, "merged.mp");
            var unreviewed = await Run(["material-graph", "merge", "--destination", paths.Destination,
                "--source", paths.Source, "--output", output]);
            Assert.Equal(2, unreviewed.Code);
            Assert.Contains("--reviewed", unreviewed.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
            foreach (string input in new[] { paths.Destination, paths.Source })
            {
                var overwrite = await Run(["material-graph", "merge", "--destination", paths.Destination,
                    "--source", paths.Source, "--output", input, "--reviewed"]);
                Assert.Equal(2, overwrite.Code);
                Assert.Equal(graph, await File.ReadAllBytesAsync(input));
            }
            byte[] retained = "existing"u8.ToArray();
            await File.WriteAllBytesAsync(output, retained);
            var exists = await Run(["material-graph", "merge", "--destination", paths.Destination,
                "--source", paths.Source, "--output", output, "--reviewed"]);
            Assert.Equal(2, exists.Code);
            Assert.Equal(retained, await File.ReadAllBytesAsync(output));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task OversizedInputsInvalidOptionsAndCancellationReturnCliErrors()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] graph = Dl1CompiledMaterialGraphMergerTests.Pack("future", (1, [1], [1])).ToArray();
            var paths = await WriteInputs(directory, graph, graph);
            var duplicate = await Run(["material-graph", "inspect", "--destination", paths.Destination,
                "--destination", paths.Destination, "--source", paths.Source]);
            Assert.Equal(2, duplicate.Code);
            var unknown = await Run(["material-graph", "inspect", "--destination", paths.Destination,
                "--source", paths.Source, "--replace"]);
            Assert.Equal(2, unknown.Code);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var canceled = await Run(["material-graph", "inspect", "--destination", paths.Destination,
                "--source", paths.Source], cancellation.Token);
            Assert.Equal(130, canceled.Code);
            Assert.Empty(canceled.Output);
            await using (FileStream oversized = new(paths.Destination, FileMode.Open, FileAccess.Write, FileShare.None))
                oversized.SetLength(256L * 1024 * 1024 + 1);
            var tooLarge = await Run(["material-graph", "inspect", "--destination", paths.Destination,
                "--source", paths.Source]);
            Assert.Equal(2, tooLarge.Code);
            Assert.Contains("256 MiB", tooLarge.Error, StringComparison.Ordinal);
            Assert.Empty(tooLarge.Output);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task DispatchAndHelpExposeTheCommands()
    {
        Assert.True(CliApplication.IsInvocation(["material-graph"]));
        Assert.True(CliApplication.IsInvocation(["MATERIAL-GRAPH"]));
        var result = await Run(["material-graph", "--help"]);
        Assert.Equal(0, result.Code);
        Assert.Contains("material-graph inspect", result.Output, StringComparison.Ordinal);
        Assert.Contains("material-graph merge", result.Output, StringComparison.Ordinal);
    }

    private static async Task<(string Destination, string Source)> WriteInputs(
        string directory, byte[] destination, byte[] source)
    {
        string destinationPath = Path.Combine(directory, "destination.mp");
        string sourcePath = Path.Combine(directory, "source.mp");
        await File.WriteAllBytesAsync(destinationPath, destination);
        await File.WriteAllBytesAsync(sourcePath, source);
        return (destinationPath, sourcePath);
    }

    private static async Task<(int Code, string Output, string Error)> Run(
        string[] args, CancellationToken cancellationToken = default)
    {
        TextWriter previousOutput = Console.Out;
        TextWriter previousError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int code = await CliApplication.RunAsync(args, cancellationToken);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
    }
}

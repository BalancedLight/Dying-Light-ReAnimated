using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ReAnimated.Codecs.Materials;

namespace ReAnimated.Cli;

internal static class MaterialGraphCommand
{
    private const int MaximumInputBytes = 256 * 1024 * 1024;
    private const string Usage = """
        Usage:
          DLReAnimated material-graph inspect --destination <materials.mp> --source <additions.mp>
          DLReAnimated material-graph merge --destination <materials.mp> --source <additions.mp> --output <new.mp> --reviewed
        """;

    public static async Task<int> RunAsync(
        string[] args, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (args.Length == 1 && args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (args.Length == 0)
            throw new ArgumentException(Usage);
        string command = args[0].ToLowerInvariant();
        if (command is not ("inspect" or "merge"))
            throw new ArgumentException(Usage);
        Dictionary<string, string> values = ParseOptions(args[1..], command == "merge");
        string destinationPath = Path.GetFullPath(Require(values, "destination"));
        string sourcePath = Path.GetFullPath(Require(values, "source"));
        string? outputPath = null;
        if (command == "merge")
        {
            if (!values.ContainsKey("reviewed"))
                throw new ArgumentException("Review the material graph plan, then pass --reviewed.");
            outputPath = NewOutput(Require(values, "output"), destinationPath, sourcePath);
        }
        ImmutableArray<byte> destination = await ReadBoundedAsync(
            destinationPath, cancellationToken).ConfigureAwait(false);
        ImmutableArray<byte> source = await ReadBoundedAsync(
            sourcePath, cancellationToken).ConfigureAwait(false);
        Dl1MaterialGraphMergePlan plan = Dl1CompiledMaterialGraphMerger.Inspect(
            destination, source, cancellationToken);
        if (command == "inspect" || !plan.CanMerge)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                format = "dl-reanimated-material-graph-inspection-v1",
                destination = InputReport(destinationPath, destination.Length, plan.DestinationSha256),
                source = InputReport(sourcePath, source.Length, plan.SourceSha256),
                plan = PlanReport(plan),
            }, options));
            return plan.CanMerge ? 0 : 2;
        }

        Dl1MaterialGraphMergeResult result = await Dl1CompiledMaterialGraphMerger.WriteNewAsync(
            outputPath!, destination, source, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            format = "dl-reanimated-material-graph-merge-v1",
            destination = InputReport(destinationPath, destination.Length, result.Plan.DestinationSha256),
            source = InputReport(sourcePath, source.Length, result.Plan.SourceSha256),
            output = new
            {
                fileName = Path.GetFileName(result.Path),
                sha256 = result.OutputSha256,
            },
            plan = PlanReport(result.Plan),
        }, options));
        return 0;
    }

    private static Dictionary<string, string> ParseOptions(string[] args, bool merge)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index] switch
            {
                "--destination" => "destination",
                "--source" => "source",
                "--output" when merge => "output",
                "--reviewed" when merge => "reviewed",
                _ => throw new ArgumentException("Choose the supported material-graph options."),
            };
            if (result.ContainsKey(name))
                throw new ArgumentException($"--{name} may be supplied only once.");
            if (name == "reviewed")
            {
                result.Add(name, "true");
                continue;
            }
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) ||
                args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"--{name} requires a value.");
            result.Add(name, args[index]);
        }
        return result;
    }

    private static string Require(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? value)
            ? value
            : throw new ArgumentException($"--{name} is required.");

    private static string NewOutput(string path, string destination, string source)
    {
        string fullPath = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (fullPath.Equals(destination, comparison) || fullPath.Equals(source, comparison))
            throw new ArgumentException("Choose a new output file distinct from both inputs.");
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new IOException("The material output already exists.");
        return fullPath;
    }

    private static async Task<ImmutableArray<byte>> ReadBoundedAsync(
        string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using FileStream input = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > MaximumInputBytes)
            throw new InvalidDataException("A material graph input exceeds the 256 MiB limit.");
        int length = checked((int)input.Length);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (input.Length != length)
            throw new IOException("A material graph input changed while reading.");
        cancellationToken.ThrowIfCancellationRequested();
        return ImmutableArray.Create(bytes);
    }

    private static object InputReport(string path, int byteLength, string sha256) =>
        new { fileName = Path.GetFileName(path), byteLength, sha256 };

    private static object PlanReport(Dl1MaterialGraphMergePlan plan) =>
        new
        {
            canMerge = plan.CanMerge,
            addedContainers = plan.AddedContainers,
            addedRecords = plan.AddedRecords,
            identicalRecords = plan.IdenticalRecords,
            conflictCount = plan.Conflicts.Length,
            conflicts = plan.Conflicts.Select(static conflict => new
            {
                container = conflict.Container,
                key = string.Create(CultureInfo.InvariantCulture, $"0x{conflict.Key:X8}"),
                destinationSha256 = conflict.DestinationSha256,
                sourceSha256 = conflict.SourceSha256,
            }),
        };
}

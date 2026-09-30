using System.Text.Json;
using System.Text.Json.Serialization;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Cli;

internal static class ModelBatchCommand
{
    public static async Task<int> RunAsync(string[] args, JsonSerializerOptions options, CancellationToken token)
    {
        if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--inspect")
            throw new ArgumentException("Usage: DLReAnimated batch-models <manifest.json> <output-parent> [--inspect]");
        var outputOptions = new JsonSerializerOptions(options);
        outputOptions.Converters.Add(new JsonStringEnumConverter());
        var manifest = Dl1ModelBatchJson.LoadManifest(args[0]);
        if (args.Length == 3)
        {
            string directory = Dl1ModelBatchRunner.ResolveRunDirectory(args[1], manifest.Id);
            Console.WriteLine(JsonSerializer.Serialize(new { mode = "recorded-receipt-only", currentInputsReverified = false,
                receipt = Dl1ModelBatchRunner.ReadReceipt(directory) }, outputOptions));
            return 0;
        }
        var result = await Dl1ModelBatchRunner.RunAsync(new() { Manifest = manifest, OutputDirectory = args[1] }, token).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(result, outputOptions));
        if (result.Receipt.Interrupted) return 130;
        return result.Receipt.Items.All(i => i.State == Dl1ModelBatchItemState.CompilerValidated) ? 0 : 2;
    }
}

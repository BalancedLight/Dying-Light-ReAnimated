using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReAnimated.Core.ModelAuthoring;

if (args.Length != 2 || args[0] is not ("write" or "check"))
    throw new ArgumentException("ModelSchemaTool write|check <dlrmodel.schema.json>");
string path = Path.GetFullPath(args[1]);
JsonObject expected = CustomModelSchema.Create();
if (args[0] == "check")
{
    if (!File.Exists(path) || !JsonNode.DeepEquals(expected, JsonNode.Parse(await File.ReadAllTextAsync(path))))
        throw new InvalidDataException("The model schema differs from the current serializer contract; regenerate it with ModelSchemaTool write.");
    Console.WriteLine("Model schema matches the current serializer contract.");
    return;
}
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
await File.WriteAllTextAsync(path, expected.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
Console.WriteLine("Wrote model schema " + CustomModelDocument.CurrentSchemaVersion + ".");

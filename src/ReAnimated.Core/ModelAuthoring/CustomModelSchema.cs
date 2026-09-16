using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>
/// Structural schema generated from the same metadata contract as model.json.
/// Cross-field geometry, ownership, evidence and freshness rules remain enforced
/// by the model/session validators rather than being reduced to type checks.
/// </summary>
public static class CustomModelSchema
{
    public static JsonObject Create()
    {
        var schema = CustomModelPackageSerializer.CreateSerializerOptions().GetJsonSchemaAsNode(
            typeof(CustomModelDocument), new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
                TransformSchemaNode = static (context, node) =>
                {
                    if (context.TypeInfo.Type == typeof(FrameRate))
                    {
                        return new JsonObject
                        {
                            ["type"] = "object", ["additionalProperties"] = false,
                            ["required"] = new JsonArray("numerator", "denominator"),
                            ["properties"] = new JsonObject
                            {
                                ["numerator"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                                ["denominator"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                            },
                        };
                    }
                    if (node is not JsonObject definition) return node;
                    if (definition["properties"] is JsonObject properties)
                    {
                        string[] calculated = context.TypeInfo.Type switch
                        {
                            Type type when type == typeof(Vector3D) => ["lengthSquared", "length", "isFinite"],
                            Type type when type == typeof(QuaternionD) => ["lengthSquared", "isFinite"],
                            Type type when type == typeof(TransformTRS) => ["isFinite"],
                            Type type when type == typeof(TransformMatrix) => ["translation", "linearDeterminant", "isFinite"],
                            _ => [],
                        };
                        foreach (string property in calculated) properties.Remove(property);
                        if (context.TypeInfo.Type == typeof(CustomModelDocument))
                        {
                            properties["schemaVersion"] = new JsonObject { ["const"] = CustomModelDocument.CurrentSchemaVersion };
                            properties["format"] = new JsonObject { ["const"] = CustomModelDocument.CurrentFormat };
                        }
                        if (context.TypeInfo.Type == typeof(RiggingSession))
                            properties["version"] = new JsonObject { ["const"] = RiggingSession.CurrentVersion };
                    }
                    if (context.PropertyInfo is { PropertyType: var propertyType, Name: var name } && propertyType == typeof(string) &&
                        (name.EndsWith("Sha256", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Fingerprint", StringComparison.OrdinalIgnoreCase) ||
                         name is "rigSignature" or "morphSignature"))
                        definition["pattern"] = "^[A-Fa-f0-9]{64}$";
                    return node;
                },
            }).AsObject();
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$id"] = "https://dl-reanimated.local/schemas/dlrmodel.schema.json";
        schema["title"] = "DL ReAnimated C# Custom Model Manifest";
        schema["description"] = "Structural schema for model.json. Numerical, ownership, capability evidence and cross-field invariants are checked by CustomModelDocument.Validate. Retail payloads are not part of this contract.";
        return schema;
    }
}

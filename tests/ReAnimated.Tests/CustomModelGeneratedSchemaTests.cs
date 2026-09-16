using System.Text.Json.Nodes;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CustomModelGeneratedSchemaTests
{
    [Fact]
    public void GeneratedContractIncludesCurrentAndLegacyFeatures()
    {
        JsonObject schema = CustomModelSchema.Create();
        JsonObject properties = schema["properties"]!.AsObject();
        Assert.Equal(CustomModelDocument.CurrentSchemaVersion, properties["schemaVersion"]!["const"]!.GetValue<int>());
        foreach (string retained in new[] { "rigConformance", "secondaryMotion", "facialPresets", "bones", "meshes", "authoredHelpers", "animationClips" })
            Assert.True(properties.ContainsKey(retained), "Missing retained field: " + retained);
        var studio = properties["riggingSession"]!["properties"]!.AsObject();
        Assert.Equal(RiggingSession.CurrentVersion, studio["version"]!["const"]!.GetValue<int>());
        Assert.Equal(7, studio["stage"]!["enum"]!.AsArray().Count);
        var recipe = studio["recipe"]!["properties"]!.AsObject();
        Assert.NotNull(recipe["assetRoles"]);
        Assert.NotNull(recipe["componentPolicies"]);
        Assert.NotNull(recipe["scalePolicy"]);
    }

    [Fact]
    public void SchemaOmitsCalculatedMathValuesAndKeepsNullableProvenance()
    {
        JsonObject schema = CustomModelSchema.Create();
        var studio = schema["properties"]!["riggingSession"]!["properties"]!;
        var vectorProperties = studio["symmetryOrigin"]!["properties"]!.AsObject();
        Assert.Equal<string>(["x", "y", "z"], vectorProperties.Select(p => p.Key));
        Assert.Contains(studio["previousSourceSha256"]!["type"]!.AsArray(), n => n!.GetValue<string>() == "null");
        Assert.Equal("^[A-Fa-f0-9]{64}$", studio["sourceSha256"]!["pattern"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }
}

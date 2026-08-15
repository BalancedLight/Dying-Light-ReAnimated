using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectJsonSchemaTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void CheckedInSchemaDeclaresSchema3AnimationContracts()
    {
        using JsonDocument schema = LoadSchema();
        JsonElement root = schema.RootElement;
        JsonElement definitions = root.GetProperty("$defs");

        Assert.Equal(
            DlraProject.CurrentSchemaVersion,
            root.GetProperty("properties")
                .GetProperty("schemaVersion")
                .GetProperty("const")
                .GetInt32());
        AssertRequired(
            root,
            "animationLibraries",
            "exportSelection");
        AssertRequired(
            definitions.GetProperty("animationSource"),
            "presentation");
        AssertRequired(
            definitions.GetProperty("animationVariant"),
            "bindingMode",
            "owningAnimationLibraryId",
            "outputAnm2Name");
        AssertRequired(
            definitions.GetProperty("boneMapping"),
            "transformComponents");

        AssertTypeContract<DlraProject>(root);
        AssertTypeContract<ProjectAnimationLibraryImport>(
            definitions.GetProperty("animationLibraryImport"));
        AssertTypeContract<ProjectAnimationLibrary>(
            definitions.GetProperty("animationLibrary"));
        AssertTypeContract<ProjectAnimationSourcePresentation>(
            definitions.GetProperty("animationSourcePresentation"));
        AssertTypeContract<ProjectExportSelection>(
            definitions.GetProperty("exportSelection"));
        AssertTypeContract<ProjectModelEntry>(
            definitions.GetProperty("modelEntry"));
        AssertTypeContract<ProjectEmbeddedAnimationStackIdentity>(
            definitions.GetProperty("embeddedAnimationStack"));
        AssertTypeContract<ProjectAnimationSource>(
            definitions.GetProperty("animationSource"));
        AssertTypeContract<ProjectAnimationVariant>(
            definitions.GetProperty("animationVariant"));
        AssertTypeContract<ProjectAnimation>(
            definitions.GetProperty("animation"));
        AssertTypeContract<ProjectAnimationSourceBinding>(
            definitions.GetProperty("animationSourceBinding"));
        AssertTypeContract<DirectBoneBinding>(
            definitions.GetProperty("directBoneBinding"));
        AssertTypeContract<DirectRigBinding>(
            definitions.GetProperty("directRigBinding"));
        AssertTypeContract<ProjectBoneMapping>(
            definitions.GetProperty("boneMapping"));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void EveryInternalSchemaReferenceResolves()
    {
        using JsonDocument schema = LoadSchema();
        JsonElement definitions = schema.RootElement.GetProperty("$defs");
        var definitionNames = definitions
            .EnumerateObject()
            .Select(static definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        var references = new List<string>();

        CollectReferences(schema.RootElement, references);

        Assert.NotEmpty(references);
        foreach (string reference in references)
        {
            const string prefix = "#/$defs/";
            Assert.StartsWith(prefix, reference, StringComparison.Ordinal);
            Assert.Contains(reference[prefix.Length..], definitionNames);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void TransformComponentSchemaAcceptsEverySupportedFlagCombination()
    {
        using JsonDocument schema = LoadSchema();
        JsonElement values = schema.RootElement
            .GetProperty("$defs")
            .GetProperty("boneMapping")
            .GetProperty("properties")
            .GetProperty("transformComponents")
            .GetProperty("enum");
        var declared = values.EnumerateArray()
            .Select(static value => value.GetString())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        RetargetTransformComponents[] supported =
        [
            RetargetTransformComponents.Translation,
            RetargetTransformComponents.Rotation,
            RetargetTransformComponents.Scale,
            RetargetTransformComponents.Translation |
                RetargetTransformComponents.Rotation,
            RetargetTransformComponents.Translation |
                RetargetTransformComponents.Scale,
            RetargetTransformComponents.Rotation |
                RetargetTransformComponents.Scale,
            RetargetTransformComponents.All,
        ];

        foreach (RetargetTransformComponents components in supported)
        {
            string serialized = JsonSerializer.Serialize(components, options);
            string enumValue = JsonSerializer.Deserialize<string>(serialized)!;
            Assert.Contains(enumValue, declared);
        }

        Assert.Equal(supported.Length, declared.Count);
        Assert.DoesNotContain("none", declared);
    }

    private static JsonDocument LoadSchema() =>
        JsonDocument.Parse(File.ReadAllText(FindRepositoryFile(
            "schemas",
            "dlraproj.schema.json")));

    private static void AssertTypeContract<T>(JsonElement schema)
    {
        var declared = schema.GetProperty("properties")
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        IEnumerable<PropertyInfo> serializedProperties = typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property =>
                property.GetMethod is { IsPublic: true } &&
                property.GetIndexParameters().Length == 0 &&
                property.GetCustomAttribute<JsonIgnoreAttribute>() is null);

        foreach (PropertyInfo property in serializedProperties)
        {
            string serializedName =
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            Assert.True(
                declared.Contains(serializedName),
                $"Schema definition is missing serialized {typeof(T).Name}." +
                $"{property.Name} property '{serializedName}'.");
        }
    }

    private static void AssertRequired(
        JsonElement schema,
        params string[] requiredNames)
    {
        var required = schema.GetProperty("required")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (string requiredName in requiredNames)
        {
            Assert.Contains(requiredName, required);
        }
    }

    private static void CollectReferences(
        JsonElement node,
        ICollection<string> references)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    if (property.NameEquals("$ref"))
                    {
                        references.Add(property.Value.GetString()!);
                    }
                    else
                    {
                        CollectReferences(property.Value, references);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in node.EnumerateArray())
                {
                    CollectReferences(item, references);
                }

                break;
        }
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{Path.Combine(relativeSegments)}' above " +
            $"'{AppContext.BaseDirectory}'.");
    }
}

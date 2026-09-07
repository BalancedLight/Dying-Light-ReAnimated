using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReAnimated.Core.ModelAuthoring;

public static class SecondaryMotionSetupSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(SecondaryMotionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        return JsonSerializer.Serialize(definition, Options);
    }

    public static SecondaryMotionDefinition Deserialize(string text, IEnumerable<string>? boneNames = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 16 * 1024 * 1024) throw new ArgumentException("Secondary setup exceeds the text limit.", nameof(text));
        SecondaryMotionDefinition definition = JsonSerializer.Deserialize<SecondaryMotionDefinition>(text, Options)
            ?? throw new JsonException("Secondary setup cannot be null.");
        definition.Validate(boneNames);
        return definition;
    }
}

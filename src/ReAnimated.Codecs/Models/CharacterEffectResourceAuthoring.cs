using System.Collections.Immutable;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public static class CharacterEffectResourceAuthoring
{
    public static ImmutableArray<Rp6lEffectDefinition> ReadRequiredDefinitions(CustomModelPackage package,
        CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        _=CustomModelPackageSerializer.Serialize(package);
        if(package.Document.CharacterResources is not { } inventory) return [];
        var selected=new Dictionary<string,Rp6lEffectDefinition>(StringComparer.OrdinalIgnoreCase);
        var bundles=new Dictionary<string,ImmutableArray<Rp6lEffectDefinition>>(StringComparer.Ordinal);
        foreach(var resource in inventory.Resources.Where(resource=>resource.Required && !resource.IsOriginalArchive &&
            Path.GetExtension(resource.LogicalName).Equals(".fx",StringComparison.OrdinalIgnoreCase)).OrderBy(resource=>resource.LogicalName,StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(resource.Status is not (CharacterDependencyStatus.Preserved or CharacterDependencyStatus.Decoded) || resource.PackedEffect is not { } receipt)
                throw new InvalidDataException("Resolve the exact packed effect before exporting: "+resource.LogicalName);
            var bundle=inventory.Resources.Single(value=>value.Id==receipt.BundleResourceId);
            if(!bundles.TryGetValue(bundle.Id,out var definitions))
            {
                definitions=Rp6lEffectBundleDecoder.Decode(package.CompanionPayloads[bundle.EntryPath!].AsSpan(),cancellationToken);
                bundles.Add(bundle.Id,definitions);
            }
            var definition=definitions.SingleOrDefault(value=>value.Name==receipt.StoredName)
                ?? throw new InvalidDataException("The packed effect definition is missing.");
            if(definition.Kind!=receipt.Kind || definition.ContentSha256!=resource.ContentSha256 ||
                definition.EntryOffset!=receipt.EntryOffset || definition.TextOffset!=receipt.TextOffset ||
                definition.EntryByteLength!=receipt.EntryByteLength || definition.TextByteLength!=receipt.TextByteLength ||
                !selected.TryAdd(resource.LogicalName,definition))
                throw new InvalidDataException("The packed effect identity is changed or ambiguous.");
        }
        foreach(var definition in selected.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntax=NativeCharacterScriptCodec.Parse(definition.SourceText);
            foreach(var argument in syntax.Calls.SelectMany(call=>call.QuotedArguments))
            {
                if(!Path.GetExtension(argument.Value).Equals(".fx",StringComparison.OrdinalIgnoreCase)) continue;
                string name=argument.Value;
                if(name!=name.Trim() || name.StartsWith('/') || name.Contains(':') || name.Contains('\\') ||
                    name.Any(char.IsControl) || name.Split('/').Any(part=>part is "" or "." or "..") || !selected.ContainsKey(name))
                    throw new InvalidDataException("Required nested effect is missing or uses an unsupported path: "+name);
            }
        }
        return selected.Values.ToImmutableArray();
    }
}

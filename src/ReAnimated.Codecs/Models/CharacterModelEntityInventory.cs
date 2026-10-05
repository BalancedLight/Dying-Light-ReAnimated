using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;

namespace ReAnimated.Codecs.Models;

public sealed record CharacterModelEntity(string Name, bool IsBone);

public static class CharacterModelEntityInventory
{
    public static ImmutableArray<CharacterModelEntity> Build(FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Package.Document.CreateEffectiveBones().Select(bone => new CharacterModelEntity(bone.Name, true))
            .Concat(FbxModelLodLayout.Create(model).Select(node => new CharacterModelEntity(node.Name, false))).ToImmutableArray();
    }

    public static CharacterModelEntity RequireUnique(FbxModelAuthoringImportResult model, string name)
    {
        var matches=Build(model).Where(entity=>string.Equals(entity.Name,name,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(matches.Length!=1)throw new InvalidDataException("The model element is missing or ambiguous: "+name);
        return matches[0];
    }

    public static ImmutableArray<string> UniqueNames(FbxModelAuthoringImportResult model)=>Build(model)
        .GroupBy(entity=>entity.Name,StringComparer.OrdinalIgnoreCase).Where(group=>group.Count()==1)
        .Select(group=>group.Single().Name).Order(StringComparer.Ordinal).ToImmutableArray();
}

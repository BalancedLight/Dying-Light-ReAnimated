using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record NativeCollisionChoice(int SourceIndex, int CommandIndex, string ResourceName, string SourceText,
    string Label, double Radius);

public static class Dl1NativeCollisionAuthoring
{
    public static ImmutableArray<NativeCollisionChoice> Inspect(SecondaryMotionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var choices = ImmutableArray.CreateBuilder<NativeCollisionChoice>();
        for (int sourceIndex = 0; sourceIndex < definition.NativeSources.Length; sourceIndex++)
        {
            NativeClothSource source = definition.NativeSources[sourceIndex];
            if (source.Kind != NativeClothSourceKind.Phx) continue;
            NativeClothSyntax syntax = Dl1ClothCodec.Parse(source.Text);
            for (int index = 0; index < syntax.Commands.Length; index++)
            {
                NativeClothCommand command = syntax.Commands[index];
                int count = command.Name switch
                {
                    "CollisionSphere" => 2, "CollisionSphereShift" => 3, "CollisionCapsuleBetween" => 5, _ => 0,
                };
                if (count == 0 || command.Arguments.Length != count || !double.TryParse(command.Arguments[^1],
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double radius) || !double.IsFinite(radius)) continue;
                choices.Add(new(sourceIndex, index, source.ResourceName, source.Text,
                    $"{source.ResourceName}: {command.Arguments[0].Trim('"')} ({index + 1})", radius));
            }
        }
        return choices.ToImmutable();
    }

    public static SecondaryMotionDefinition SetRadius(SecondaryMotionDefinition definition, NativeCollisionChoice choice,
        double radius)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(choice);
        if ((uint)choice.SourceIndex >= (uint)definition.NativeSources.Length ||
            definition.NativeSources[choice.SourceIndex].Text != choice.SourceText ||
            definition.NativeSources[choice.SourceIndex].ResourceName != choice.ResourceName)
            throw new InvalidOperationException("The native source changed. Select its current collision again.");
        NativeClothSource source = definition.NativeSources[choice.SourceIndex];
        if (source.Kind != NativeClothSourceKind.Phx)
            throw new InvalidOperationException("Select a PHX collision declaration.");
        string text = Dl1ClothCodec.ReplaceCollisionRadius(source.Text, choice.CommandIndex, radius);
        return definition with { NativeSources = definition.NativeSources.SetItem(choice.SourceIndex,
            source with { Text = text, OriginalText = source.OriginalText ?? source.Text }) };
    }
}

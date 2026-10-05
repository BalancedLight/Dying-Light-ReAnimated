using System.Collections.Immutable;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterCompletenessSummaryTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RepeatedBlockerTextSummarizesOnceWithoutDiscardingResourceProvenance()
    {
        CharacterResourceInventory baseline = ModelsWorkspaceMorphAuthoringTests
            .CreateGenericDifferentTopologyTargetPackage().Document.CharacterResources!;
        CharacterResourceRecord first = Missing("missing-one", "data/unknown.def", "Could not resolve.", "parent-one");
        CharacterResourceRecord second = Missing("missing-two", "data/unknown.def", "Could not resolve.", "parent-two");
        CharacterResourceRecord other = Missing("missing-other", "data/another.def", "Other failure.", "parent-three");
        CharacterResourceRecord caseDifferent = Missing("missing-case", "data/unknown.def", "could not resolve.", "parent-four");
        CharacterResourceInventory inventory = baseline with
        {
            Resources = baseline.Resources.AddRange(ImmutableArray.Create(first, second, other, caseDifferent)),
            Subsystems = Enum.GetValues<CharacterSubsystem>().Select(subsystem =>
                new CharacterSubsystemReview(subsystem, CharacterDependencyStatus.NotApplicable,
                    "Reviewed separately.")).ToImmutableArray(),
            CompiledSemanticSha256 = new string('a', 64),
            LoadedResourceSha256 = new string('a', 64),
            VerifiedPlayerScenarios = ["stock-reuse", "facial", "ragdoll", "gore"],
        };
        inventory.Validate();

        Assert.Equal(3, inventory.ExportBlockers.Length);
        Assert.Equal(1, inventory.ExportBlockers.Count(message =>
            message == "Damage: data/unknown.def: Could not resolve."));
        Assert.Contains("Damage: data/another.def: Other failure.", inventory.ExportBlockers);
        Assert.Contains("Damage: data/unknown.def: could not resolve.", inventory.ExportBlockers);
        Assert.False(inventory.IsDependencyComplete);
        Assert.False(inventory.IsGameReady);
        Assert.Equal(baseline.Resources.Length + 4, inventory.Resources.Length);
        Assert.Equal(["parent-one"], inventory.Resources.Single(resource => resource.Id == first.Id).ReferencedBy.ToArray());
        Assert.Equal(["parent-two"], inventory.Resources.Single(resource => resource.Id == second.Id).ReferencedBy.ToArray());
        Assert.Equal(["parent-three"], inventory.Resources.Single(resource => resource.Id == other.Id).ReferencedBy.ToArray());
        Assert.Equal(["parent-four"], inventory.Resources.Single(resource => resource.Id == caseDifferent.Id).ReferencedBy.ToArray());
        Assert.All(inventory.Resources.Where(resource => resource.Id.StartsWith("missing-", StringComparison.Ordinal)),
            resource =>
            {
                Assert.True(resource.Required);
                Assert.Equal(CharacterDependencyStatus.Missing, resource.Status);
                Assert.Null(resource.EntryPath);
            });
    }

    private static CharacterResourceRecord Missing(string id, string logicalName, string detail, string parent) => new()
    {
        Id = id,
        LogicalName = logicalName,
        ProviderIdentity = "synthetic-provider",
        SourceFingerprint = new string('b', 64),
        Subsystem = CharacterSubsystem.Damage,
        Status = CharacterDependencyStatus.Missing,
        Required = true,
        Detail = detail,
        ReferencedBy = [parent],
    };
}

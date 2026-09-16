using System.Collections.Immutable;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RiggingValidationTests
{
    internal static string Hash(char digit) => new(digit, 64);
    internal static RigValidationScope Scope() => new()
    {
        SourceSha256 = Hash('a'), InputFingerprint = Hash('b'), AuthoredContractSha256 = Hash('c'),
        ProfileId = "synthetic-human", ProfileVersion = "1", BuildFingerprint = Hash('d'),
        CompilerFingerprint = Hash('e'), CompiledAssetSha256 = Hash('f'), LoadedResourceSha256 = Hash('f'),
        LoadedResourceId = "test-provider:mesh", RuntimeSessionId = "capture-1", RuntimeActorId = "actor-1",
        ClipSha256 = Hash('1'), ScenarioId = "walk-flat", ScenarioFingerprint = Hash('2'),
    };

    internal static CapabilityValidationReceipt Passed(RigValidationFacet facet, RigValidationScope? scope = null)
    {
        RigValidationScope identity = scope ?? Scope();
        return new()
        {
            CapabilityId = "locomotion", Facet = facet, Status = RigValidationStatus.Passed, Scope = identity,
            ObservedUtc = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Evidence = [new RigEvidenceReference
            {
                Id = "run/" + facet, ArtifactSha256 = Hash('3'), BuildFingerprint = identity.BuildFingerprint,
                Kind = facet switch
                {
                    RigValidationFacet.CompiledVerification => RigEvidenceKind.CompiledReadBack,
                    RigValidationFacet.LoadedResourceIdentity => RigEvidenceKind.LoadedResourceCapture,
                    RigValidationFacet.LiveScenario => RigEvidenceKind.LiveScenarioCapture,
                    _ => RigEvidenceKind.OfflineTest,
                },
            }],
        };
    }

    [Fact]
    public void MissingEvidenceLeavesAllSevenFacetsUnverified()
    {
        var result = RigCapabilityValidation.Assess("locomotion", Scope(), []);
        Assert.Equal(7, result.Facets.Length);
        Assert.Equal(RigValidationStatus.Unverified, result.Status);
        Assert.All(result.Facets, f => Assert.Equal(RigValidationStatus.Unverified, f.Status));
    }

    [Fact]
    public void CompiledSuccessDoesNotCertifyLoadedResourceOrGameplay()
    {
        var receipts = Enum.GetValues<RigValidationFacet>().Where(f => f <= RigValidationFacet.CompiledVerification).Select(f => Passed(f));
        var result = RigCapabilityValidation.Assess("locomotion", Scope(), receipts);
        Assert.Equal(RigValidationStatus.Passed, result.Facets.Single(f => f.Facet == RigValidationFacet.CompiledVerification).Status);
        Assert.Equal(RigValidationStatus.Unverified, result.Facets.Single(f => f.Facet == RigValidationFacet.LoadedResourceIdentity).Status);
        Assert.Equal(RigValidationStatus.Unverified, result.Facets.Single(f => f.Facet == RigValidationFacet.LiveScenario).Status);
        Assert.Equal(RigValidationStatus.Unverified, result.Status);
    }

    [Fact]
    public void CompleteScopedEvidencePassesAllApplicableFacets()
    {
        var result = RigCapabilityValidation.Assess("locomotion", Scope(), Enum.GetValues<RigValidationFacet>().Select(f => Passed(f)));
        Assert.Equal(RigValidationStatus.Passed, result.Status);
        Assert.All(result.Facets, f => Assert.NotNull(f.ReceiptId));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("input")]
    [InlineData("contract")]
    [InlineData("profile")]
    [InlineData("profile-case")]
    [InlineData("build")]
    [InlineData("compiler")]
    [InlineData("compiled")]
    [InlineData("provider")]
    [InlineData("session")]
    [InlineData("actor")]
    [InlineData("clip")]
    [InlineData("scenario")]
    [InlineData("scenario-inputs")]
    public void ChangedIdentitiesCannotReuseACompleteResult(string change)
    {
        var original = Scope();
        var current = change switch
        {
            "source" => original with { SourceSha256 = Hash('0') },
            "input" => original with { InputFingerprint = Hash('0') },
            "contract" => original with { AuthoredContractSha256 = Hash('0') },
            "profile" => original with { ProfileVersion = "2" },
            "profile-case" => original with { ProfileId = "Synthetic-human" },
            "build" => original with { BuildFingerprint = Hash('0') },
            "compiler" => original with { CompilerFingerprint = Hash('0') },
            "compiled" => original with { CompiledAssetSha256 = Hash('0'), LoadedResourceSha256 = Hash('0') },
            "provider" => original with { LoadedResourceId = "other-provider:mesh" },
            "session" => original with { RuntimeSessionId = "capture-2" },
            "actor" => original with { RuntimeActorId = "actor-2" },
            "clip" => original with { ClipSha256 = Hash('0') },
            "scenario" => original with { ScenarioId = "walk-slope" },
            "scenario-inputs" => original with { ScenarioFingerprint = Hash('0') },
            _ => throw new InvalidOperationException(),
        };
        var result = RigCapabilityValidation.Assess("locomotion", current, Enum.GetValues<RigValidationFacet>().Select(f => Passed(f, original)));
        Assert.Equal(RigValidationStatus.Unverified, result.Status);
    }

    [Fact]
    public void HexCasingDoesNotChangeContentIdentity()
    {
        Assert.True(Scope().Matches(Scope() with { SourceSha256 = Hash('A') }, RigValidationFacet.GeometryAndBind));
    }

    [Theory]
    [InlineData(RigValidationFacet.GeometryAndBind, RigEvidenceKind.UserOverride)]
    [InlineData(RigValidationFacet.RuntimeNodeCompleteness, RigEvidenceKind.GeometryInference)]
    [InlineData(RigValidationFacet.LoadedResourceIdentity, RigEvidenceKind.CompiledReadBack)]
    [InlineData(RigValidationFacet.LiveScenario, RigEvidenceKind.LoadedResourceCapture)]
    public void WrongEvidenceTierCannotMakeAFacetPass(RigValidationFacet facet, RigEvidenceKind kind)
    {
        var receipt = Passed(facet);
        receipt = receipt with { Evidence = [receipt.Evidence[0] with { Kind = kind }] };
        Assert.Throws<ArgumentException>(receipt.Validate);
    }

    [Fact]
    public void ARequiredFacetCannotBeSkippedByItsReceipt()
    {
        var history = Enum.GetValues<RigValidationFacet>().Select(f => Passed(f)).ToArray();
        history[^1] = history[^1] with { Status = RigValidationStatus.NotApplicable, Reason = "No live test available." };
        Assert.Equal(RigValidationStatus.Unverified, RigCapabilityValidation.Assess("locomotion", Scope(), history).Status);
        var partial = Enum.GetValues<RigValidationFacet>().Select(f => new RigFacetRequirement(f, f != RigValidationFacet.LiveScenario,
            f == RigValidationFacet.LiveScenario ? "This source-only inspection capability declares no gameplay behavior." : null));
        Assert.Equal(RigValidationStatus.Passed, RigCapabilityValidation.Assess("locomotion", Scope(), history, partial).Status);
    }

    [Fact]
    public void RequiredFacetDeclarationsCannotOmitTheMissingNativeGates()
    {
        Assert.Throws<ArgumentException>(() => RigCapabilityValidation.Assess("locomotion", Scope(), [], [new(RigValidationFacet.GeometryAndBind)]));
    }

    [Fact]
    public void CurrentFailureWinsAndHistoryRemainsAvailable()
    {
        var history = Enum.GetValues<RigValidationFacet>().Select(f => Passed(f)).ToImmutableArray();
        var failed = Passed(RigValidationFacet.LiveScenario) with
        {
            Status = RigValidationStatus.Failed, Reason = "Contact moved with absolute world elevation.",
            ObservedUtc = new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero),
        };
        var result = RigCapabilityValidation.Assess("locomotion", Scope(), history.Add(failed));
        Assert.Equal(RigValidationStatus.Failed, result.Status);
        Assert.Equal(7, history.Length);
    }

    [Fact]
    public void LoadProofRequiresExpectedContentAndPhysicalCaptureIdentity()
    {
        var valid = Passed(RigValidationFacet.LoadedResourceIdentity);
        Assert.Throws<ArgumentException>(() => (valid with { Scope = valid.Scope with { LoadedResourceSha256 = Hash('0') } }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { Scope = valid.Scope with { RuntimeActorId = null } }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with { Evidence = [valid.Evidence[0] with { BuildFingerprint = Hash('0') }] }).Validate());
    }

    [Fact]
    public void DuplicateAndUnprovenReceiptsAreRejected()
    {
        var receipt = Passed(RigValidationFacet.GeometryAndBind);
        Assert.Throws<ArgumentException>(() => RigCapabilityValidation.Assess("locomotion", Scope(), [receipt, receipt]));
        Assert.Throws<ArgumentException>(() => (receipt with { Evidence = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (receipt with { Scope = receipt.Scope with { AuthoredContractSha256 = null } }).Validate());
    }
}

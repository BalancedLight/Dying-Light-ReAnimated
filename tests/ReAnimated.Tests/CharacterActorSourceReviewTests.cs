using System.IO;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class CharacterActorSourceReviewTests
{
    [Fact]
    public void SharedPresetImportsRemainOutsideSelectedActorScope()
    {
        const string source = "import \"shared.def\"\nPresetDef(\"Character\") { Preset(\"one\") { SetField(\"MeshName\", \"body.msh\"); } " +
            "Preset(\"other\") { SetField(\"MeshName\", \"other.msh\"); } }";
        var review = CharacterActorSourceReview.Review(source, new(Sha(source), "body.msh", 3, new(2, "Preset", "one")));
        Assert.Equal(source, review.Document.Write());
        Assert.Empty(review.ScopedReferences);
        Assert.Contains(review.UnverifiedLeads, reference => reference.CallName == "import" && reference.Value == "shared.def");
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void SelectedActorAndModelExcludeSiblingActorReferences()
    {
        const string source = "Actor(\"first\") { Model(\"body_a.msh\"); PhysicsScript(\"first.phx\"); }\n" +
            "Actor(\"second\") { Model(\"body_b.msh\"); PhysicsScript(\"second.phx\"); }";
        CharacterActorSourceReviewResult review = CharacterActorSourceReview.Review(source,
            new(Sha(source), "body_a.msh", 1, new(0, "Actor", "first")));

        Assert.Equal(source, review.Document.Write());
        Assert.Equal(Sha(source), review.SourceSha256);
        Assert.Equal(1, review.ModelCallIndex);
        Assert.Equal(0, review.ModelArgumentIndex);
        Assert.Equal("\"body_a.msh\"", source.Substring(review.ModelTokenStart, review.ModelTokenLength));
        CharacterActorQuotedReference scoped = Assert.Single(review.ScopedReferences);
        Assert.Equal("first.phx", scoped.Value);
        Assert.Equal("\"first.phx\"", source.Substring(scoped.ArgumentStart, scoped.ArgumentLength));
        Assert.Contains(review.UnverifiedLeads, r => r.Value == "second.phx");
        Assert.DoesNotContain(review.ScopedReferences, r => r.Value == "second.phx");
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void NestedSiblingBlockAndSecondModelDoNotLeakIntoSelectedModelScope()
    {
        const string source = "Actor(\"one\") { Model(\"body.msh\") { PhysicsScript(\"inside.phx\"); } " +
            "Part(\"other\") { PhysicsScript(\"sibling.phx\"); } " +
            "Model(\"extra.msh\") { PhysicsScript(\"other.phx\"); } PhysicsScript(\"peer.phx\"); }";
        CharacterActorSourceReviewResult review = CharacterActorSourceReview.Review(source,
            new(Sha(source), "body.msh", 1, new(0, "Actor", "one")));

        Assert.Equal("inside.phx", Assert.Single(review.ScopedReferences).Value);
        Assert.Contains(review.UnverifiedLeads, r => r.Value == "sibling.phx");
        Assert.Contains(review.UnverifiedLeads, r => r.Value == "other.phx");
        Assert.Contains(review.UnverifiedLeads, r => r.Value == "peer.phx");
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void DuplicateModelNameRequiresExactCallSelection()
    {
        const string source = "Actor(\"one\") { Model(\"body.msh\") { BehaviorSet(\"first.phx\"); } " +
            "Model(\"body.msh\") { BehaviorSet(\"second.phx\"); } }";
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Review(source,
            new(Sha(source), "body.msh", EnclosingActor: new(0, "Actor", "one"))));

        CharacterActorSourceReviewResult review = CharacterActorSourceReview.Review(source,
            new(Sha(source), "body.msh", 1, new(0, "Actor", "one")));
        Assert.Equal("first.phx", Assert.Single(review.ScopedReferences).Value);
        Assert.Contains(review.UnverifiedLeads, r => r.Value == "second.phx");
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void StaleHashTokenCallAndEnclosingScopeFailClosed()
    {
        const string source = "Actor(\"one\") { Model(\"body.msh\"); PhysicsScript(\"one.phx\"); } " +
            "Actor(\"two\") { Model(\"other.msh\"); }";
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Review(source,
            new(Sha(source + "changed"), "body.msh", 1, new(0, "Actor", "one"))));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Review(source,
            new(Sha(source), "body.msh", 4, new(0, "Actor", "one"))));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Review(source,
            new(Sha(source), "body.msh", 1, new(0, "Actor", "wrong"))));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Review(source,
            new(Sha(source), "body.msh", 1, new(3, "Actor", "two"))));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Review(source,
            new(Sha(source), "body.msh", 1, new(0, "Actor", "one"), ExpectedModelTokenStart: 0,
                ExpectedModelTokenLength: "\"body.msh\"".Length)));
        Assert.Throws<ArgumentException>(() => CharacterActorSourceReview.Review(source,
            new("not-a-sha", "body.msh")));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void OnlyClearlyInheritedIncludeIsScopedAndUnsupportedCallRemainsLead()
    {
        const string source = "// original spacing retained\n!include(\"shared.def\");\n" +
            "Model(\"body.msh\") { UnknownResource(\"mystery.phx\"); BehaviorSet(\"cloth.phx\"); }";
        CharacterActorSourceReviewResult review = CharacterActorSourceReview.Review(source,
            new(Sha(source), "body.msh"));

        Assert.Equal(source, review.Document.Write());
        Assert.Contains(review.ScopedReferences, r => r.Value == "shared.def");
        CharacterActorQuotedReference cloth = Assert.Single(review.ScopedReferences, r => r.Value == "cloth.phx");
        Assert.Equal("BehaviorSet(\"cloth.phx\")", source.Substring(cloth.CallStart, cloth.CallLength));
        Assert.Contains(review.UnverifiedLeads, r => r.Value == "mystery.phx");

        var request = new CharacterActorSourceReviewRequest(review.SourceSha256, "body.msh", review.ModelCallIndex,
            ExpectedModelTokenStart: review.ModelTokenStart, ExpectedModelTokenLength: review.ModelTokenLength);
        var expected = new CharacterActorReferenceExpectation(cloth.CallIndex, cloth.ArgumentIndex,
            cloth.ArgumentStart, cloth.ArgumentLength, cloth.Value);
        Assert.Equal(review.SourceSha256, CharacterActorSourceReview.Revalidate(source, request, [expected]).SourceSha256);
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Revalidate(source, request,
            [expected with { SourceStart = expected.SourceStart + 1 }]));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Revalidate(source, request,
            [new(review.UnverifiedLeads[0].CallIndex, review.UnverifiedLeads[0].ArgumentIndex,
                review.UnverifiedLeads[0].ArgumentStart, review.UnverifiedLeads[0].ArgumentLength,
                review.UnverifiedLeads[0].Value)]));
    }

    [Fact]
    public void PresetMeshNameUsesValueTokenAndOnlyWhitelistedDirectResourceFields()
    {
        const string source = "PresetDef(\"Character\") { Preset(\"first\") { SetField(\"MeshName\", \"actors/body.msh\"); " +
            "SetField(\"m_FaceMimicFile\", \"face.fed\"); SetField(\"m_MpcScript\", \"cloth.mpcloth\"); " +
            "SetField(\"UnknownResource\", \"lead.phx\"); Preset(\"nested\") { SetField(\"MeshName\", \"child.msh\"); " +
            "SetField(\"PhysicsScript\", \"nested.phx\"); } } Preset(\"other\") { SetField(\"MeshName\", \"other.msh\"); " +
            "SetField(\"m_FaceMimicFile\", \"other.fed\"); } }";
        var review = CharacterActorSourceReview.Review(source,
            new(Sha(source), "actors/body.msh", 2, new(1, "Preset", "first")));
        Assert.Equal(source, review.Document.Write());
        Assert.Equal(1, review.ModelArgumentIndex);
        Assert.Equal("\"actors/body.msh\"", source.Substring(review.ModelTokenStart, review.ModelTokenLength));
        Assert.Equal(2, review.ScopedReferences.Length);
        Assert.Contains(review.ScopedReferences, reference => reference.Value == "face.fed" && reference.ArgumentIndex == 1);
        Assert.Contains(review.ScopedReferences, reference => reference.Value == "cloth.mpcloth" && reference.ArgumentIndex == 1);
        Assert.Contains(review.UnverifiedLeads, reference => reference.Value == "lead.phx");
        Assert.Contains(review.UnverifiedLeads, reference => reference.Value == "nested.phx");
        Assert.Contains(review.UnverifiedLeads, reference => reference.Value == "other.fed");
        Assert.Contains(review.UnverifiedLeads, reference => reference.Value == "child.msh");
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Review(source,
            new(Sha(source), "actors/body.msh", 2, new(0, "PresetDef", "Character"))));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Review(source,
            new(Sha(source), "actors/body.msh", 2)));
    }

    [Fact]
    public void DuplicatePresetModelsRequireCallAndPresetSelectionAndRetainStaleGuards()
    {
        const string source = "PresetDef(\"Character\") { Preset(\"first\") { SetField(\"MeshName\", \"body.msh\"); } " +
            "Preset(\"second\") { SetField(\"MeshName\", \"body.msh\"); } }";
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Review(source,
            new(Sha(source), "body.msh", EnclosingActor: new(1, "Preset", "first"))));
        var selected = CharacterActorSourceReview.Review(source,
            new(Sha(source), "body.msh", 2, new(1, "Preset", "first")));
        Assert.Equal(1, selected.ModelArgumentIndex);
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Review(source,
            new(Sha(source), "body.msh", 4, new(1, "Preset", "first"))));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Review(source,
            new(Sha(source + " "), "body.msh", 2, new(1, "Preset", "first"))));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceReview.Review(source,
            new(Sha(source), "body.msh", 2, new(1, "Preset", "first"),
                selected.ModelTokenStart + 1, selected.ModelTokenLength)));
    }

    private static string Sha(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

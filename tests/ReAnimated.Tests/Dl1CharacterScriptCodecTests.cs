using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1CharacterScriptCodecTests
{
    [Fact]
    public void BareImportRetainsQuotedSpansCommentsAndFollowingPresetScope()
    {
        const string source = "import \"base.def\" // keep\r\nimport \"other.def\";\r\n" +
            "PresetDef(\"Character\") { Preset(\"one\") { SetField(\"MeshName\", \"body.msh\"); } }";
        var parsed = NativeCharacterScriptCodec.Parse(source);
        Assert.Equal(source, parsed.Write());
        Assert.Equal("import", parsed.Calls[0].Name);
        Assert.Equal(-1, parsed.Calls[0].ParentCallIndex);
        var token = Assert.Single(parsed.Calls[0].QuotedArguments);
        Assert.Equal("base.def", token.Value);
        Assert.Equal("\"base.def\"", source.Substring(token.Start, token.Length));
        Assert.Equal(2, parsed.Calls[3].ParentCallIndex);
        Assert.Equal(3, parsed.Calls[4].ParentCallIndex);
        Assert.Equal(source.Replace("\"base.def\"", "\"replacement.def\"", StringComparison.Ordinal),
            parsed.ReplaceQuotedArguments([new(0, 0, "base.def", "replacement.def")]).Write());
    }

    [Theory]
    [InlineData("import plain.def")]
    [InlineData("import \"unterminated")]
    public void MalformedBareImportsAreRefused(string source) =>
        Assert.Throws<FormatException>(() => NativeCharacterScriptCodec.Parse(source));

    private const string RagdollSource = """
        // Generic authored fixture; no retail script data.
        !include("physics_base.phx")
        PhysicsParams() { QuickStepNumIterations(12) FutureSetting("retain") }
        RagdollParams() { Mass(15) }
        Bones() {
            UseBone("root", "sphere", 3)
            UseBoneScale("arm", "capsule", 2, 0.75)
            CollisionHelper("hand", "arm", 1.25)
            BoneSynchro("root", "arm")
        }
        Joints() {
            DefineJoint("root", "arm", "hinge", 0, 1, 0, 0, 0, 0)
            Set1DOFStops("root arm", -0.1, 0.3, 0)
        }
        """;

    [Fact]
    public void GenericSyntaxRetainsCommentsUnknownCallsAndTargetedTokenEdits()
    {
        const string source = "// before\r\nSection() { Known(\"old\") Unknown([1,2], \"stay\") }\r\n";
        NativeCharacterScriptDocument parsed = NativeCharacterScriptCodec.Parse(source);
        Assert.Equal(source, parsed.Write());
        Assert.Equal("Section", parsed.Calls[1].ParentCallIndex is 0
            ? parsed.Calls[0].Name : string.Empty);
        string changed = parsed.ReplaceQuotedArguments(
            [new NativeCharacterTokenReplacement(1, 0, "old", "new")]).Write();
        Assert.Equal(source.Replace("Known(\"old\")", "Known(\"new\")", StringComparison.Ordinal), changed);
        Assert.Throws<ArgumentException>(() => parsed.ReplaceQuotedArguments(
            [new NativeCharacterTokenReplacement(1, 0, "stale", "new")]));
    }

    [Fact]
    public void NumericTokenEditPreservesEveryOtherSourceCharacter()
    {
        const string source = "RagdollParams() { Mass(3.00) /* authored */ Future(9) }\r\r\n";
        NativeCharacterScriptDocument parsed = NativeCharacterScriptCodec.Parse(source);
        string changed = parsed.ReplaceNumericArguments(
            [new NativeCharacterNumericReplacement(1, 0, "3.00", 4.25)]).Write();
        Assert.Equal(source.Replace("Mass(3.00)", "Mass(4.25)", StringComparison.Ordinal), changed);
        Assert.Throws<ArgumentException>(() => parsed.ReplaceNumericArguments(
            [new NativeCharacterNumericReplacement(1, 0, "3", 4.25)]));
    }

    [Fact]
    public void NumericEditsRetainFloatTypingAndExplicitIntegerFields()
    {
        const string source = "Values(0.75, 3, 2.0) // keep\r\n";
        var parsed = NativeCharacterScriptCodec.Parse(source);
        Assert.Equal("Values(1.0, 3, 2.0) // keep\r\n", parsed.ReplaceNumericArguments(
            [new(0, 0, "0.75", 1)]).Write());
        Assert.Equal("Values(0.75, 1.0, 4) // keep\r\n", parsed.ReplaceNumericArguments(
            [new(0, 1, "3", 1) { Kind = NativeCharacterNumericKind.RealNumber },
             new(0, 2, "2.0", 4) { Kind = NativeCharacterNumericKind.WholeNumber }]).Write());
        Assert.Throws<ArgumentException>(() => parsed.ReplaceNumericArguments(
            [new(0, 1, "3", 1.5) { Kind = NativeCharacterNumericKind.WholeNumber }]));
        Assert.Throws<ArgumentException>(() => parsed.ReplaceNumericArguments(
            [new(0, 0, "0.75", double.MaxValue) { Kind = NativeCharacterNumericKind.RealNumber }]));
    }

    [Fact]
    public void RagdollReaderRetainsBoneJointCollisionSyncAndRelativeNumbers()
    {
        Dl1RagdollDocument read = Dl1RagdollCodec.Read(
            RagdollSource, ["root", "arm", "hand"], ["physics_base.phx"]);
        Assert.True(read.IsValid);
        Assert.Equal(RagdollSource, read.Syntax.Write());
        Assert.Equal(2, read.Bones.Length);
        Assert.Equal(2, read.Bones[1].RelativeMass);
        Assert.Equal(0.75, read.Bones[1].RadiusMultiplier);
        Assert.Equal(6, Assert.Single(read.Joints).AxisValues.Length);
        Assert.Contains(read.CollisionSettings, setting => setting.Name == "CollisionHelper");
        Assert.Contains(read.SynchronizationSettings, setting => setting.Name == "BoneSynchro");
        Assert.Contains(read.JointSettings, setting => setting.Name == "Set1DOFStops");
        Assert.Contains(read.References, reference =>
            reference.Kind == NativeCharacterReferenceKind.IncludeResource &&
            reference.Name == "physics_base.phx");
        Assert.Contains(read.PhysicsSettings, setting => setting.Name == "FutureSetting");
    }

    [Fact]
    public void RagdollRenameUpdatesVerifiedAtomicAndJointPairTokensOnly()
    {
        Dl1RagdollDocument read = Dl1RagdollCodec.Read(RagdollSource);
        NativeCharacterRenameResult renamed = read.RenameBone(
            "arm", "arm_new", ["root", "arm_new", "hand"]);
        Assert.True(renamed.IsValid);
        Assert.Contains("UseBoneScale(\"arm_new\"", renamed.Document.Write());
        Assert.Contains("Set1DOFStops(\"root arm_new\"", renamed.Document.Write());
        Assert.Contains("FutureSetting(\"retain\")", renamed.Document.Write());
        Assert.DoesNotContain("\"arm\"", renamed.Document.Write());
        Assert.False(read.RenameBone("arm", "missing", ["root"]).IsValid);
        Assert.False(read.RenameBone("arm", "root", ["root", "arm"]).IsValid);

        Dl1RagdollDocument unclassified = Dl1RagdollCodec.Read(
            RagdollSource + "Unknown(\"arm\")\n");
        NativeCharacterRenameResult refused = unclassified.RenameBone(
            "arm", "arm_new", ["root", "arm_new", "hand"]);
        Assert.False(refused.IsValid);
        Assert.Equal(unclassified.Syntax.Write(), refused.Document.Write());
        Assert.Contains(refused.Diagnostics, diagnostic =>
            diagnostic.Code == "ragdoll_rename_unclassified_reference");
    }

    [Fact]
    public void BodyElementsReaderRetainsRelicsEffectsDisableAndMeatDependencies()
    {
        const string source = """
            !include("body_rules.def")
            DestroyedHeadParts("fragment_XX.msh", 3)
            MeatParts(LIGHT, CUT, HEAD) { AddMeatPart("fragment.msh", 2) }
            BodyElement(_ARM, 1, 0, 0., 25., "helper_arm")
            AddRelics("Arm", PHYSICS_SINGLE, "arm_relic.phx", "blood.fx", [0,0,0], [0,0,0])
            AddMesh2Disable("arm_mesh")
            UnknownStatement("keep") // unchanged
            """;
        Dl1BodyElementsDocument read = Dl1BodyElementsCodec.Read(source,
            ["helper_arm"], ["arm_relic.phx", "blood.fx"]);
        Assert.True(read.IsValid);
        Assert.Equal(source, read.Syntax.Write());
        Assert.Equal("_ARM", Assert.Single(read.Elements).ElementToken);
        Assert.Equal("arm_relic.phx", Assert.Single(read.Relics).PhysicsResource);
        Assert.Equal("blood.fx", Assert.Single(read.Relics).EffectResource);
        Assert.Equal("arm_mesh", Assert.Single(read.MeshDisables).EntityName);
        Assert.Equal("fragment.msh", Assert.Single(read.MeatParts).MeshResource);
        Assert.Contains(read.References, reference =>
            reference.Kind == NativeCharacterReferenceKind.MeshResource &&
            reference.Name == "fragment_XX.msh");
        NativeCharacterRenameResult renamed = read.RenameReference(
            NativeCharacterReferenceKind.PhysicsResource,
            "arm_relic.phx", "new_relic.phx", ["new_relic.phx"]);
        Assert.True(renamed.IsValid);
        Assert.Contains("\"new_relic.phx\"", renamed.Document.Write());
        Assert.Contains("UnknownStatement(\"keep\") // unchanged", renamed.Document.Write());
        Assert.False(read.RenameReference(NativeCharacterReferenceKind.PhysicsResource,
            "arm_relic.phx", "dangling.phx", ["new_relic.phx"]).IsValid);
    }

    [Fact]
    public void DamageReaderRetainsMeasuredXformAndGenericPatchFlag()
    {
        const string source = """
            !Damage(s, s)
            Damage("patch_arm", "helper_arm") {
                Xform(1,0,0,0,1,0,0,0,1,0.1,0.2,0.3)
                UseGenericPatch()
                FutureDamageCall("keep")
            }
            """;
        Dl1DamagePatchDocument read = Dl1DamagePatchCodec.Read(source, ["helper_arm"]);
        Assert.True(read.IsValid);
        Assert.Equal(source, read.Syntax.Write());
        Dl1DamagePatch patch = Assert.Single(read.Patches);
        Assert.Equal(12, patch.XformValues.Length);
        Assert.Equal(0.3, patch.XformValues[11]);
        Assert.True(patch.UseGenericPatch);
        NativeCharacterRenameResult renamed = read.RenamePatch(
            "patch_arm", "patch_new", ["patch_new"]);
        Assert.True(renamed.IsValid);
        Assert.Contains("Damage(\"patch_new\", \"helper_arm\")", renamed.Document.Write());
        Assert.Contains("FutureDamageCall(\"keep\")", renamed.Document.Write());
        Assert.False(read.RenamePatch("patch_arm", "patch_missing", ["patch_new"]).IsValid);
        Assert.Contains(Dl1DamagePatchCodec.Read(source.Replace("helper_arm", "absent", StringComparison.Ordinal),
            ["helper_arm"]).Diagnostics, diagnostic => diagnostic.Code == "damage_helper_missing");
    }

    [Theory]
    [InlineData("Section() {")]
    [InlineData("Section() }")]
    [InlineData("Section(\"unterminated)")]
    [InlineData("/* unterminated")]
    [InlineData("Section(1,,2)")]
    public void MalformedSourceIsRejected(string source) =>
        Assert.Throws<FormatException>(() => NativeCharacterScriptCodec.Parse(source));

    [Fact]
    public void TypedReadersReportMalformedOrDanglingReferences()
    {
        Assert.False(Dl1RagdollCodec.Read("Bones(){ UseBoneScale(\"root\", \"capsule\", 2) }").IsValid);
        Assert.False(Dl1BodyElementsCodec.Read("BodyElement(_ARM, 1, 0, 0, 2, \"absent\")",
            ["helper"]).IsValid);
        Assert.False(Dl1DamagePatchCodec.Read(
            "Damage(\"patch\", \"helper\") { Xform(1,2) }").IsValid);
    }
}

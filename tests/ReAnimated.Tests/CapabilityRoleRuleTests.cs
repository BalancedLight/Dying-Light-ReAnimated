using System.Collections.Immutable;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CapabilityRoleRuleTests
{
    internal static FbxModelAuthoringImportResult Model()
    {
        var source = StructuralHelperAuthoringTests.Source(); var doc = source.Package.Document; var session = doc.RiggingSession!;
        var marker = session.Recipe.Entities.Single(e => e.NativeName == "normal_marker_2");
        var root = session.Recipe.Entities.Single(e => e.NativeName == "Root");
        var profile = RigCapabilityProfileSerializer.Seal(new()
        {
            Identity = new() { Id = "generic-frame-checks", Version = "1", ContentSha256 = new string('0', 64) }, FamilyId = "synthetic",
            Roles =
            [
                new() { Id = "root", NativeName = "Root", Requirement = RigRoleRequirementKind.Optional, OwnerAssetRoleId = "character" },
                new() { Id = "marker", NativeName = marker.NativeName, Requirement = RigRoleRequirementKind.Required, EntityKind = RigNativeEntityKind.Helper,
                    OwnerAssetRoleId = "character", SkinInfluenceAllowed = false, ValidationRules = new()
                    {
                        Frame = new() { AllowedPolicies = [RigFramePolicy.Manual], OriginRoleId = "root", OriginOffset = new(.2, .3, .4),
                            DirectionRoleId = "root", LocalAxis = new(-.2, -.3, -.4), AngularToleranceDegrees = .1 },
                        Bounds = new() { AllowedPolicies = [RigBoundsPolicy.GenerateSegmentProxy] },
                        Channels = new() { EmittedMask = RigAnimationComponents.None, PositionOwners = [RigComponentOwner.BindInherited],
                            RotationOwners = [RigComponentOwner.BindInherited], ScaleOwners = [RigComponentOwner.BindInherited] },
                        Retention = new() { MustEmit = true, AnimationLod = RigAnimationLod.Off },
                    } },
            ],
            Capabilities = [new() { Id = "partial", RoleIds = ["marker"] }],
        });
        var components = new AnimationComponentPolicy { EntityId = marker.EntityId, EmittedMask = RigAnimationComponents.None, AnimationLod = RigAnimationLod.Off,
            Position = new() { Owners = [RigComponentOwner.BindInherited] }, Rotation = new() { Owners = [RigComponentOwner.BindInherited] }, Scale = new() { Owners = [RigComponentOwner.BindInherited] } };
        doc = doc with { RiggingSession = session with { Recipe = session.Recipe with
        {
            Profile = profile.Identity, ProfileSnapshot = profile, SelectedCapabilityIds = ["partial"],
            Assignments = [new("root", root.EntityId), new("marker", marker.EntityId)], ComponentPolicies = [components],
        } } };
        return source with { Package = source.Package with { Document = doc } };
    }

    private static FbxModelAuthoringImportResult ChangeRule(FbxModelAuthoringImportResult source, Func<RigRoleValidationRules, RigRoleValidationRules> change)
    {
        var doc = source.Package.Document; var session = doc.RiggingSession!; var profile = session.Recipe.ProfileSnapshot!;
        profile = RigCapabilityProfileSerializer.Seal(profile with { Roles = profile.Roles.Select(r => r.Id == "marker" ? r with { ValidationRules = change(r.ValidationRules!) } : r).ToImmutableArray() });
        return source with { Package = source.Package with { Document = doc with { RiggingSession = session with { Recipe = session.Recipe with { Profile = profile.Identity, ProfileSnapshot = profile } } } } };
    }

    [Fact]
    public void ChildDirectionReferenceAndParentRoleAreNotAConstructionCycle()
    {
        var model = Model(); var recipe = model.Package.Document.RiggingSession!.Recipe; var profile = recipe.ProfileSnapshot!;
        profile = RigCapabilityProfileSerializer.Seal(profile with { Roles = profile.Roles.Select(r => r.Id == "root"
            ? r with { ValidationRules = new() { Frame = new() { AllowedPolicies = [RigFramePolicy.PreserveSource], DirectionRoleId = "marker" } } }
            : r with { ParentRoleId = "root", ParentConstraint = RigRoleParentConstraint.Direct }).ToImmutableArray() });
        var closure = RigProfileResolver.Resolve(profile, ["partial"], recipe.Assignments.Select(a => a.RoleId));
        Assert.DoesNotContain(closure.Diagnostics, d => d.Code == "role-cycle");
        Assert.Equal(2, closure.Roles.Length); Assert.All(closure.Roles, r => Assert.True(r.Required));
        var contract = Dl1CustomModelRigPreparer.Prepare(model).Contract;
        var foreign = new Dl1AuthoredRigContract(contract.SourceModelName, new string('a', 64), contract.Nodes, contract.MorphChannels);
        Assert.Throws<InvalidDataException>(() => Dl1CapabilityRuleValidator.Assess(model.Package.Document, foreign));
    }

    [Fact]
    public void MissingConsumerDiagnosticsCanBeSerializedInExportReceipts()
    {
        var review = FbxCapabilityProfileAuthoring.Inspect(Model());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(review));
        Assert.NotEqual(0, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
        Assert.All(review.Diagnostics, d => Assert.False(d.ConsumerIds.IsDefault));
    }

    [Fact]
    public void RelativeFrameChecksIncludeTheirReferenceRoleInClosure()
    {
        var model = Model(); var review = FbxCapabilityProfileAuthoring.Inspect(model);
        Assert.DoesNotContain(review.Diagnostics, d => d.Status == RigValidationStatus.Failed);
        Assert.Equal(4, review.PassedRuleChecks.Count(c => c.RoleId == "marker"));
        Assert.DoesNotContain("source frame preserved", review.PassedRuleChecks.Single(c => c.RoleId == "marker" && c.Check == "frame").Observation, StringComparison.Ordinal);
        Assert.True(review.Roles.Single(r => r.Id == "root").Required);
        Assert.Contains(review.Diagnostics, d => d.Status == RigValidationStatus.Unverified);
        Assert.Empty(model.Package.Document.RiggingSession!.ValidationHistory);
        Assert.Throws<OperationCanceledException>(() => FbxCapabilityProfileAuthoring.Inspect(model, new(true)));
    }

    [Theory]
    [InlineData("origin", "role-frame-origin-conflict")]
    [InlineData("direction", "role-frame-direction-conflict")]
    [InlineData("policy", "role-frame-policy-conflict")]
    [InlineData("bounds", "role-bounds-conflict")]
    [InlineData("channels", "role-channels-conflict")]
    [InlineData("owners", "role-channels-conflict")]
    [InlineData("lod", "role-retention-conflict")]
    public void DeclaredConstraintsRejectConflictsWithoutChangingTheModel(string kind, string code)
    {
        var original = Model();
        var model = ChangeRule(original, rules => kind switch
        {
            "origin" => rules with { Frame = rules.Frame! with { OriginOffset = new(2, 3, 4) } },
            "direction" => rules with { Frame = rules.Frame! with { LocalAxis = new(.2, .3, .4) } },
            "policy" => rules with { Frame = rules.Frame! with { AllowedPolicies = [RigFramePolicy.Camera] } },
            "bounds" => rules with { Bounds = rules.Bounds! with { MinimumHalfExtents = new(10, 10, 10) } },
            "channels" => rules with { Channels = rules.Channels! with { EmittedMask = RigAnimationComponents.Rotation } },
            "owners" => rules with { Channels = rules.Channels! with { RotationOwners = [RigComponentOwner.Procedural] } },
            "lod" => rules with { Retention = rules.Retention! with { AnimationLod = RigAnimationLod.Lod0 } },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
        var review = FbxCapabilityProfileAuthoring.Inspect(model);
        Assert.Contains(review.Diagnostics, d => d.Code == code && d.RoleId == "marker" && d.EntityId is not null);
        Assert.Throws<InvalidDataException>(() => FbxCapabilityProfileAuthoring.ValidateExport(model));
        Assert.Equal<FbxModelSurface>(original.Surfaces, model.Surfaces);
        Assert.Same(original.AnimationClips, model.AnimationClips);
        Assert.Equal<CustomModelBone>(original.Package.Document.Bones, model.Package.Document.Bones);
    }

    [Fact]
    public void PreparedAffineOverridesCannotBypassSourceOrAxisRequirements()
    {
        foreach (bool preserve in new[] { true, false })
        {
            var model = ChangeRule(Model(), r => r with { Frame = r.Frame! with { PreserveSourceGlobal = preserve, RequireOrthonormal = !preserve } });
            var doc = model.Package.Document; var session = doc.RiggingSession!;
            Guid id = session.Recipe.Assignments.Single(a => a.RoleId == "marker").EntityId;
            var frame = new TransformTRS(preserve ? new(.4, .3, .4) : new(.2, .3, .4), QuaternionD.Identity, preserve ? Vector3D.One : new(2, 1, 1)).ToMatrix();
            var policy = new RigEntityFramePolicy { EntityId = id, FramePolicy = RigFramePolicy.Manual, SolvedGlobalFrame = frame };
            doc = doc with { RiggingSession = session with { Recipe = session.Recipe with { FramePolicies = session.Recipe.FramePolicies.Where(p => p.EntityId != id).Append(policy).ToImmutableArray() } } };
            var changed = model with { Package = model.Package with { Document = doc } };
            Assert.Contains(FbxCapabilityProfileAuthoring.Inspect(changed).Diagnostics,
                d => d.Code == (preserve ? "role-source-frame-conflict" : "role-frame-orthonormal-conflict"));
        }
    }

    [Fact]
    public void RuleIdsAndAnimationLodCannotStandInForUnobservedRequirements()
    {
        var model = ChangeRule(Model(), _ => new());
        var review = FbxCapabilityProfileAuthoring.Inspect(model);
        Assert.Empty(review.PassedRuleChecks.Where(c => c.RoleId == "marker"));
        foreach (string check in new[] { "frame", "bounds", "channels", "retention" })
            Assert.Contains(review.Diagnostics, d => d.RoleId == "marker" && d.Code == "role-" + check + "-rule-unverified");
        var source = Model(); var doc = source.Package.Document; var session = doc.RiggingSession!; var profile = session.Recipe.ProfileSnapshot!;
        profile = RigCapabilityProfileSerializer.Seal(profile with { Roles = profile.Roles.Select(r => r.Id == "marker" ? r with { RequiredLods = [1], RequiredVariantIds = ["alternate"] } : r).ToImmutableArray() });
        var variant = source with { Package = source.Package with { Document = doc with { RiggingSession = session with { Recipe = session.Recipe with { Profile = profile.Identity, ProfileSnapshot = profile } } } } };
        Assert.Contains(FbxCapabilityProfileAuthoring.Inspect(variant).Diagnostics, d => d.Code == "role-variant-retention-unverified");
    }

    [Fact]
    public void RulesPersistWithTheirVectorsAndRejectInvalidDefinitions()
    {
        var profile = Model().Package.Document.RiggingSession!.Recipe.ProfileSnapshot!;
        var bytes = RigCapabilityProfileSerializer.Serialize(profile);
        var restored = RigCapabilityProfileSerializer.Deserialize(bytes);
        Assert.Equal(new(.2, .3, .4), restored.Roles.Single(r => r.Id == "marker").ValidationRules!.Frame!.OriginOffset);
        Assert.Equal(profile.Identity, restored.Identity);
        Assert.Throws<ArgumentException>(() => ChangeRule(Model(), rules => rules with { Frame = rules.Frame! with { LocalAxis = Vector3D.Zero } }));
        Assert.Throws<ArgumentException>(() => ChangeRule(Model(), rules => rules with { Frame = rules.Frame! with { OriginRoleId = "absent" } }));
        Assert.Throws<ArgumentException>(() => ChangeRule(Model(), rules => rules with { Channels = rules.Channels! with { RotationOwners = [RigComponentOwner.Unknown] } }));
    }
}

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigSetupTransferTests : IDisposable
{
    private readonly string directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void SerializedSetupUsesPortableSelectorsWithoutDonorIdentities()
    {
        (FbxModelAuthoringImportResult donor, RigSetupPreset setup) = Donor();
        byte[] bytes = RigSetupPresetSerializer.Serialize(setup);
        Assert.Equal<byte>(bytes, RigSetupPresetSerializer.Serialize(RigSetupPresetSerializer.Capture(donor.Package.Document, setup.Name)));
        string json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(donor.Package.Document.ModelId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        foreach (Guid entityId in donor.Package.Document.RiggingSession!.Recipe.Entities.Select(e => e.EntityId))
            Assert.DoesNotContain(entityId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceSha256", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("artifactSha256", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(donor.Package.Document.Source.ContentSha256, json, StringComparison.OrdinalIgnoreCase);
        Assert.All(setup.Nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.Key)));
        Assert.All(setup.Nodes, node => Assert.DoesNotContain("entityId", node.Key, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FreshReorderedDestinationResolvesByRoleAndNameRatherThanPosition()
    {
        (FbxModelAuthoringImportResult donor, RigSetupPreset setup) = Donor();
        FbxModelAuthoringImportResult destination = DestinationWithFreshReorderedEntities(setup.Profile);
        var donorIds = donor.Package.Document.RiggingSession!.Recipe.Entities
            .Where(entity => setup.Nodes.Any(node => node.NativeName == entity.NativeName))
            .Select(entity => entity.EntityId).ToHashSet();
        Assert.NotEmpty(donorIds);
        Assert.DoesNotContain(destination.Package.Document.RiggingSession!.Recipe.Entities,
            entity => donorIds.Contains(entity.EntityId));
        ImmutableArray<RigSetupMatch> matches = FbxRigSetupTransfer.Propose(destination, setup);
        Assert.Equal(setup.Nodes.Length, matches.Length);
        Assert.All(matches, match => Assert.NotNull(match.DestinationEntityId));
        Assert.All(matches, match => Assert.Equal(match.Name, destination.Package.Document.RiggingSession!.Recipe.Entities.Single(e => e.EntityId == match.DestinationEntityId).NativeName));
        Assert.NotEqual(setup.Nodes[0].Key, matches[0].DestinationEntityId?.ToString("N"));

        var mapping = matches.ToDictionary(match => match.Key, match => match.DestinationEntityId!.Value);
        FbxModelAuthoringImportResult previewTarget = destination;
        RigSetupTransferPreview preview = FbxRigSetupTransfer.Preview(previewTarget, setup, mapping);
        Assert.Equal<FbxModelSurface>(destination.Surfaces, preview.Candidate.Surfaces);
        Assert.Same(destination.Rig, preview.Candidate.Rig);
        Assert.Equal<byte>(destination.Package.SourceFbx, preview.Candidate.Package.SourceFbx);
        Assert.Equal(destination.AnimationClips, preview.Candidate.AnimationClips);
        Assert.Equal(destination.Package.Document.RiggingSession!.Recipe.ScalePolicy, preview.Candidate.Package.Document.RiggingSession!.Recipe.ScalePolicy);
        Assert.Equal<CustomModelAuthoredHelper>(destination.Package.Document.AuthoredHelpers, preview.Candidate.Package.Document.AuthoredHelpers);
        Assert.Equal<HelperRecipe>(destination.Package.Document.RiggingSession.Recipe.Helpers, preview.Candidate.Package.Document.RiggingSession.Recipe.Helpers);
        var transferred = preview.Candidate.Package.Document.RiggingSession.Recipe.ComponentPolicies.Single(p => p.EntityId == mapping[setup.Nodes.Single(n => n.Channels is not null).Key]);
        Assert.All(transferred.Position.Evidence, e =>
        {
            Assert.Equal(RigEvidenceKind.UserOverride, e.Kind);
            Assert.Equal(destination.Package.Document.Source.ContentSha256, e.ArtifactSha256);
            Assert.Contains(setup.ContentSha256, e.Description, StringComparison.Ordinal);
            Assert.NotEqual("donor-channel-evidence", e.Id);
        });
        Assert.Contains(preview.Candidate.Package.Document.CreateEffectiveBones(), bone => bone.Name == "unknown_extra");
        Assert.Contains(preview.Review.Diagnostics, diagnostic => diagnostic.Status is RigValidationStatus.Unverified or RigValidationStatus.Failed);
    }

    [Fact]
    public void DuplicateUnresolvedKindAndForeignOwnerMappingsAreRejected()
    {
        (_, RigSetupPreset setup) = Donor();
        FbxModelAuthoringImportResult destination = DestinationWithFreshReorderedEntities(setup.Profile);
        ImmutableArray<RigSetupMatch> matches = FbxRigSetupTransfer.Propose(destination, setup);
        var resolved = matches.ToDictionary(match => match.Key, match => match.DestinationEntityId!.Value);
        string firstKey = setup.Nodes[0].Key;
        string secondKey = setup.Nodes[1].Key;
        resolved[secondKey] = resolved[firstKey];
        Assert.Throws<InvalidOperationException>(() => FbxRigSetupTransfer.Preview(destination, setup, resolved));

        resolved[secondKey] = matches.Single(match => match.Key == secondKey).DestinationEntityId!.Value;
        resolved.Remove(firstKey);
        Assert.Throws<InvalidOperationException>(() => FbxRigSetupTransfer.Preview(destination, setup, resolved));

        resolved[firstKey] = matches.Single(match => match.Key == firstKey).DestinationEntityId!.Value;
        var wrongKind = destination.Package.Document.RiggingSession!.Recipe.Entities
            .Single(entity => entity.EntityId == resolved[firstKey]);
        var kindDoc = ReplaceEntity(destination, wrongKind with { Kind = RigNativeEntityKind.Mesh });
        Assert.Throws<InvalidOperationException>(() => FbxRigSetupTransfer.Preview(kindDoc, setup, resolved));

        Guid foreignId = Guid.NewGuid();
        var foreignEntity = wrongKind with { EntityId = foreignId, OwnerAssetId = Guid.NewGuid() };
        var foreignDoc = ReplaceEntity(destination, foreignEntity);
        resolved[firstKey] = foreignId;
        Assert.Throws<InvalidOperationException>(() => FbxRigSetupTransfer.Preview(foreignDoc, setup, resolved));
    }

    [Fact]
    public void PreviewCannotApplyToChangedTargetAndPresetTamperingIsRejected()
    {
        (_, RigSetupPreset setup) = Donor();
        FbxModelAuthoringImportResult destination = DestinationWithFreshReorderedEntities(setup.Profile);
        var matches = FbxRigSetupTransfer.Propose(destination, setup);
        var mapping = matches.ToDictionary(match => match.Key, match => match.DestinationEntityId!.Value);
        RigSetupTransferPreview preview = FbxRigSetupTransfer.Preview(destination, setup, mapping);
        FbxModelAuthoringImportResult changed = destination with { Rig = destination.Rig };
        Assert.False(FbxRigSetupTransfer.TryApply(changed, preview, out _));

        Assert.Throws<ArgumentException>(() => RigSetupPresetSerializer.Verify(setup with { Name = "tampered" }));
        Assert.Throws<ArgumentException>(() => RigSetupPresetSerializer.Verify(setup with
            { ContentSha256 = new string('0', 64) }));
    }

    [Fact]
    public void TransferredChoicesSurvivePackageSaveAndReopenWithoutGeometryOrClipChanges()
    {
        (_, RigSetupPreset setup) = Donor();
        FbxModelAuthoringImportResult destination = DestinationWithFreshReorderedEntities(setup.Profile);
        var matches = FbxRigSetupTransfer.Propose(destination, setup);
        var mapping = matches.ToDictionary(match => match.Key, match => match.DestinationEntityId!.Value);
        RigSetupTransferPreview preview = FbxRigSetupTransfer.Preview(destination, setup, mapping);
        Assert.True(FbxRigSetupTransfer.TryApply(destination, preview, out FbxModelAuthoringImportResult applied));
        string path = Path.Combine(directory, "transferred-setup.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(applied.Package, path);
        FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(
            CustomModelPackageSerializer.Load(path));
        RiggingSession reopenedSession = reopened.Package.Document.RiggingSession!;
        Assert.Equal(setup.Profile.Identity, reopenedSession.Recipe.Profile);
        Assert.Equal<string>(setup.Capabilities, reopenedSession.Recipe.SelectedCapabilityIds);
        Assert.Contains(reopenedSession.Recipe.Assignments, assignment => setup.Nodes[0].Roles.Contains(assignment.RoleId));
        Assert.Equal(JsonSerializer.Serialize(destination.Surfaces), JsonSerializer.Serialize(reopened.Surfaces));
        // The fixture assigns a fresh model identity without reimporting the FBX, so
        // source stack IDs are rekeyed on reopen. Compare source fingerprints and
        // decoded tracks instead of requiring stale in-memory IDs to survive.
        Assert.Equal(destination.Package.Document.ModelId, reopened.Package.Document.ModelId);
        Assert.Equal(destination.Package.Document.AnimationClips.Select(clip => clip.SourceFingerprint).Order(StringComparer.Ordinal),
            reopened.Package.Document.AnimationClips.Select(clip => clip.SourceFingerprint).Order(StringComparer.Ordinal));
        foreach (CustomModelAnimationClip sourceMetadata in destination.Package.Document.AnimationClips)
        {
            CustomModelAnimationClip reopenedMetadata = reopened.Package.Document.AnimationClips.Single(clip =>
                clip.SourceFingerprint == sourceMetadata.SourceFingerprint);
            AnimationClip clip = destination.AnimationClips[sourceMetadata.Id];
            ImmutableArray<TransformTrack> actualTracks = reopened.AnimationClips[reopenedMetadata.Id].TransformTracks;
            Assert.Equal(clip.TransformTracks.Length, actualTracks.Length);
            for (int i = 0; i < clip.TransformTracks.Length; i++)
            {
                Assert.Equal(clip.TransformTracks[i].BoneIndex, actualTracks[i].BoneIndex);
                Assert.Equal<TransformKeyframe>(clip.TransformTracks[i].Keyframes, actualTracks[i].Keyframes);
            }
        }
        Assert.Equal<byte>(destination.Package.SourceFbx, reopened.Package.SourceFbx);
        Assert.Contains(FbxCapabilityProfileAuthoring.Inspect(reopened).Diagnostics,
            diagnostic => diagnostic.Status is RigValidationStatus.Unverified or RigValidationStatus.Failed);
    }

    private static (FbxModelAuthoringImportResult Donor, RigSetupPreset Setup) Donor()
    {
        FbxModelAuthoringImportResult source = StructuralHelperAuthoringTests.Source();
        RigCapabilityProfile profile = CapabilityProfileWorkflowTests.Profile();
        var recipe = source.Package.Document.RiggingSession!.Recipe;
        Guid marker = recipe.Entities.Single(entity => entity.NativeName == "normal_marker_2").EntityId;
        Guid body = recipe.Entities.Single(entity => entity.NativeName == "Child").EntityId;
        FbxModelAuthoringImportResult profiled = FbxCapabilityProfileAuthoring.Preview(source, profile,
            ["partial", "body"], [new("marker", marker), new("body", body)], recipe.AssetRoles).Candidate;
        RiggingSession session = profiled.Package.Document.RiggingSession!;
        RigEvidenceReference evidence = new()
        {
            Id = "donor-channel-evidence",
            Kind = RigEvidenceKind.UserOverride,
            ArtifactSha256 = profiled.Package.Document.Source.ContentSha256,
            Description = "Source-specific donor channel evidence must not enter a portable setup.",
        };
        var channel = new AnimationComponentPolicy
        {
            EntityId = marker,
            Position = new() { Owners = [RigComponentOwner.BindInherited], Evidence = [evidence] },
            Rotation = new() { Owners = [RigComponentOwner.BindInherited], Evidence = [evidence] },
            Scale = new() { Owners = [RigComponentOwner.BindInherited], Evidence = [evidence] },
            EmittedMask = RigAnimationComponents.None,
            AnimationLod = RigAnimationLod.Off,
            LodEvidence = [evidence],
        };
        session = RiggingSessions.Change(session, session with
        {
            Recipe = session.Recipe with { ComponentPolicies = [channel] },
        }, RiggingEditKind.Profile);
        profiled = profiled with { Package = profiled.Package with
            { Document = profiled.Package.Document with { RiggingSession = session } } };
        return (profiled, RigSetupPresetSerializer.Capture(profiled.Package.Document, "portable setup"));
    }

    private static FbxModelAuthoringImportResult DestinationWithFreshReorderedEntities(
        RigCapabilityProfile profile)
    {
        FbxModelAuthoringImportResult destination = StructuralHelperAuthoringTests.Source();
        var document = destination.Package.Document with { ModelId = Guid.NewGuid(), RiggingSession = null };
        document = CustomModelHelperAuthoring.SetLocalTransform(document, document.AuthoredHelpers[0].Id,
            new(new(.8, .9, 1.1), ReAnimated.Core.Mathematics.QuaternionD.Identity, ReAnimated.Core.Mathematics.Vector3D.One));
        RiggingSession session = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig);
        destination = destination with { Package = destination.Package with { Document = document with { RiggingSession = session } } };
        var entities = session.Recipe.Entities.Reverse().ToImmutableArray();
        var assignments = session.Recipe.Assignments.ToList();
        assignments.Add(new("marker", entities.Single(entity => entity.NativeName == "normal_marker_2").EntityId));
        assignments.Add(new("body", entities.Single(entity => entity.NativeName == "Child").EntityId));
        var owners = session.Recipe.AssetRoles;
        session = RiggingSessions.Change(session, session with
        {
            Recipe = session.Recipe with
            {
                Profile = profile.Identity,
                ProfileSnapshot = profile,
                SelectedCapabilityIds = ["partial", "body"],
                Entities = entities,
                Assignments = assignments.ToImmutableArray(),
                AssetRoles = owners,
                ScalePolicy = session.Recipe.ScalePolicy with { RuntimeUniformBodyScale = 1.25 },
            },
        }, RiggingEditKind.Profile);
        return destination with { Package = destination.Package with
            { Document = destination.Package.Document with { RiggingSession = session } }, Rig = destination.Rig };
    }

    private static FbxModelAuthoringImportResult ReplaceEntity(
        FbxModelAuthoringImportResult source, RigEntityBinding replacement)
    {
        RiggingSession session = source.Package.Document.RiggingSession!;
        var entities = session.Recipe.Entities.ToBuilder();
        int index = -1;
        for (int i = 0; i < entities.Count; i++)
            if (entities[i].EntityId == replacement.EntityId) { index = i; break; }
        if (index >= 0) entities[index] = replacement;
        else entities.Add(replacement);
        RiggingSession changed = session with { Recipe = session.Recipe with { Entities = entities.ToImmutable() } };
        return source with { Package = source.Package with
            { Document = source.Package.Document with { RiggingSession = changed } } };
    }

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(directory);
}

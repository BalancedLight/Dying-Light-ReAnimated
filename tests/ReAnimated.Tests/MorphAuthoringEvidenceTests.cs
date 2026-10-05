using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class MorphAuthoringEvidenceTests
{
    private const string Expression = "face_expression";
    private const uint Descriptor = 0x1234;

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void FractionalNeutralFingerprintAndAcceptedReceiptAreCultureInvariant()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            FbxModelAuthoringImportResult accepted = AcceptedExpression(fractionalNeutral: true);
            string surfaceId = Assert.Single(accepted.Surfaces).Id;
            MorphAuthoringRecord receipt = Assert.Single(accepted.Package.Document.MorphAuthoringRecords);
            string english = CharacterGeometryAuthoring.CreateMorphProfile(accepted, surfaceId).TopologyFingerprint;
            Assert.Equal(receipt.TargetNeutralFingerprint, english);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string german = CharacterGeometryAuthoring.CreateMorphProfile(accepted, surfaceId).TopologyFingerprint;
            Assert.Equal(english, german);
            Assert.True(Assert.Single(MorphAuthoringEvidence.ReconcileTarget(accepted)
                .Package.Document.MorphAuthoringRecords).Accepted);
            Assert.Empty(MorphAuthoringEvidence.ExportBlockers(accepted));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void UnchangedAcceptedExpressionSurvivesCaptureSaveAndReopen()
    {
        FbxModelAuthoringImportResult accepted = AcceptedExpression();
        MorphAuthoringRecord before = Assert.Single(accepted.Package.Document.MorphAuthoringRecords);
        Assert.True(before.Accepted);
        Assert.Equal(5, before.OriginalSourceChannelIndex);
        Assert.Equal(0, before.TargetChannelSlot);
        Assert.Equal(Descriptor, before.DescriptorHash);
        Assert.Empty(MorphAuthoringEvidence.ExportBlockers(accepted));
        Assert.Same(accepted, MorphAuthoringEvidence.ReconcileTarget(accepted));

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "accepted.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(accepted.Package, path);
            FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(
                CustomModelPackageSerializer.Load(path));
            MorphAuthoringRecord after = Assert.Single(reopened.Package.Document.MorphAuthoringRecords);
            Assert.True(after.Accepted);
            Assert.Equal(before.ReferenceSourceSha256, after.ReferenceSourceSha256);
            Assert.Equal(before.TargetNeutralFingerprint, after.TargetNeutralFingerprint);
            Assert.Equal(before.OriginalSourceChannelIndex, after.OriginalSourceChannelIndex);
            Assert.Empty(MorphAuthoringEvidence.ExportBlockers(reopened));
            Assert.Equal(Assert.Single(accepted.Surfaces).Vertices.Select(v => v.Position),
                Assert.Single(reopened.Surfaces).Vertices.Select(v => v.Position));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ZeroReplacementIsRefusedButKeepExistingPreservesAcceptedShapeAndReceipt()
    {
        FbxModelAuthoringImportResult accepted = AcceptedExpression();
        FbxModelSurface face = Assert.Single(accepted.Surfaces);
        MorphAuthoringRecord receipt = Assert.Single(accepted.Package.Document.MorphAuthoringRecords);
        FbxModelMorphTarget originalShape = Assert.Single(face.MorphTargets);
        ImmutableArray<Vector3D> zero = Enumerable.Repeat(Vector3D.Zero, face.Vertices.Length).ToImmutableArray();

        Assert.Throws<InvalidOperationException>(() => CharacterGeometryAuthoring.SetExpression(accepted,
            face.Id, Expression, Descriptor, zero, MorphTransferConflict.Reject, reviewed: true));
        Assert.Throws<InvalidDataException>(() => CharacterGeometryAuthoring.SetExpression(accepted,
            face.Id, Expression, Descriptor, zero, MorphTransferConflict.ReplaceExisting, reviewed: true));
        FbxModelAuthoringImportResult kept = CharacterGeometryAuthoring.SetExpression(accepted,
            face.Id, Expression, Descriptor, zero, MorphTransferConflict.KeepExisting, reviewed: true);

        Assert.Same(accepted, kept);
        Assert.Same(accepted.Package, kept.Package);
        Assert.Same(receipt, Assert.Single(kept.Package.Document.MorphAuthoringRecords));
        Assert.Same(originalShape, Assert.Single(Assert.Single(kept.Surfaces).MorphTargets));
        Assert.Contains(originalShape.PositionDeltas, delta => delta.Length > 0);
        Assert.Equal(face.Vertices.Select(vertex => vertex.Position),
            Assert.Single(kept.Surfaces).Vertices.Select(vertex => vertex.Position));
        Assert.Empty(MorphAuthoringEvidence.ExportBlockers(kept));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void LegacyAcceptedZeroPayloadBecomesUnreviewedWithoutDeletingItsEvidence()
    {
        FbxModelAuthoringImportResult accepted = AcceptedExpression();
        FbxModelSurface originalSurface = Assert.Single(accepted.Surfaces);
        FbxModelMorphTarget originalShape = Assert.Single(originalSurface.MorphTargets);
        Assert.Contains(originalShape.PositionDeltas, delta => delta.Length > 0);
        ImmutableArray<Vector3D> zero = Enumerable.Repeat(Vector3D.Zero, originalSurface.Vertices.Length).ToImmutableArray();
        FbxModelSurface zeroSurface = originalSurface with
        {
            MorphTargets = [originalShape with { PositionDeltas = zero }],
        };
        FbxModelAuthoringImportResult legacy = accepted with
        {
            Surfaces = [zeroSurface],
            Package = accepted.Package with { Document = accepted.Package.Document with
            {
                MorphSignature = FbxModelAuthoringImporter.ComputeMorphSignature(
                    accepted.Package.Document.MorphChannels, [zeroSurface]),
            } },
        };

        FbxModelAuthoringImportResult reconciled = MorphAuthoringEvidence.ReconcileTarget(legacy);
        MorphAuthoringRecord receipt = Assert.Single(reconciled.Package.Document.MorphAuthoringRecords);
        Assert.False(receipt.Accepted);
        Assert.Equal(Assert.Single(accepted.Package.Document.MorphAuthoringRecords).ReferenceSourceSha256,
            receipt.ReferenceSourceSha256);
        Assert.All(Assert.Single(Assert.Single(reconciled.Surfaces).MorphTargets).PositionDeltas,
            delta => Assert.Equal(Vector3D.Zero, delta));
        Assert.Single(MorphAuthoringEvidence.ExportBlockers(reconciled));
        Assert.Contains(Assert.Single(Assert.Single(accepted.Surfaces).MorphTargets).PositionDeltas,
            delta => delta.Length > 0);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void NeutralEditInvalidatesReviewButPreservesReceiptAndNonzeroExpression()
    {
        FbxModelAuthoringImportResult accepted = AcceptedExpression();
        MorphAuthoringRecord prior = Assert.Single(accepted.Package.Document.MorphAuthoringRecords);
        FbxModelSurface face = Assert.Single(accepted.Surfaces);
        FbxModelSurface shifted = face with { Vertices = face.Vertices.SetItem(0,
            face.Vertices[0] with { Position = face.Vertices[0].Position + new Vector3D(0.125, 0, 0) }) };
        FbxModelAuthoringImportResult changed = accepted with { Surfaces = [shifted] };
        FbxModelAuthoringImportResult captured = ModelGeometryRevisionCodec.Capture(changed);

        MorphAuthoringRecord stale = Assert.Single(captured.Package.Document.MorphAuthoringRecords);
        Assert.False(stale.Accepted);
        Assert.Equal(prior.ReferenceSourceSha256, stale.ReferenceSourceSha256);
        Assert.Equal(prior.TargetNeutralFingerprint, stale.TargetNeutralFingerprint);
        Assert.Equal(prior.OriginalSourceChannelIndex, stale.OriginalSourceChannelIndex);
        Assert.Equal(prior.TargetChannelSlot, stale.TargetChannelSlot);
        Assert.Equal(prior.DescriptorHash, stale.DescriptorHash);
        Assert.Contains(Assert.Single(shifted.MorphTargets).PositionDeltas, delta => delta.Length > 0);
        Assert.Equal(Assert.Single(face.MorphTargets).PositionDeltas.ToArray(),
            Assert.Single(shifted.MorphTargets).PositionDeltas.ToArray());
        Assert.Single(MorphAuthoringEvidence.ExportBlockers(captured));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ChangedTopologyMissingSurfaceAndOutOfRangeConstraintsEachStayBlockedOnce()
    {
        FbxModelAuthoringImportResult accepted = AcceptedExpression();
        FbxModelSurface surface = Assert.Single(accepted.Surfaces);
        MorphAuthoringRecord receipt = Assert.Single(accepted.Package.Document.MorphAuthoringRecords);
        ImmutableArray<uint> swapped = surface.Indices.SetItem(0, surface.Indices[1]).SetItem(1, surface.Indices[0]);
        AssertStale(accepted with { Surfaces = [surface with { Indices = swapped }] });
        AssertStale(accepted with { Surfaces = [surface with { Id = "another-surface" }] });
        AssertStale(WithReceipt(accepted, receipt with { ReviewedTargetRegion = [999] }));
        AssertStale(WithReceipt(accepted, receipt with { LockedTargetVertices = [999] }));
        AssertStale(WithReceipt(accepted, receipt with { ReviewedLandmarks =
            [new("surface/0", 0, "surface/0", 999)] }));
        AssertStale(WithReceipt(accepted, receipt with { ReviewedTriangles =
            [new("surface/0", 0, "surface/0", 999)] }));

        FbxModelAuthoringImportResult unreviewed = WithReceipt(accepted, receipt with { Accepted = false });
        Assert.Single(MorphAuthoringEvidence.ExportBlockers(unreviewed));
        Assert.False(Assert.Single(MorphAuthoringEvidence.ReconcileTarget(unreviewed)
            .Package.Document.MorphAuthoringRecords).Accepted);
    }

    [Theory]
    [InlineData("neutral")]
    [InlineData("deltas")]
    [InlineData("slot")]
    [InlineData("legacy")]
    [Trait("ValidationTier", "Hermetic")]
    public async Task StandardSourceWriteRefusesStaleExpressionBeforeEmittingFiles(string changedField)
    {
        FbxModelAuthoringImportResult accepted = AcceptedExpression();
        FbxModelSurface face = Assert.Single(accepted.Surfaces);
        var receipt = Assert.Single(accepted.Package.Document.MorphAuthoringRecords);
        FbxModelAuthoringImportResult changed = changedField switch
        {
            "neutral" => accepted with { Surfaces = [face with { Vertices = face.Vertices.SetItem(1,
                face.Vertices[1] with { Position = face.Vertices[1].Position + new Vector3D(0, 0.125, 0) }) }] },
            "deltas" => accepted with { Surfaces = [face with { MorphTargets = [Assert.Single(face.MorphTargets) with
                { PositionDeltas = Assert.Single(face.MorphTargets).PositionDeltas.SetItem(1, new(0.5, 0, 0)) }] }] },
            "slot" => WithReceipt(accepted, receipt with { TargetChannelSlot = 1 }),
            "legacy" => WithReceipt(accepted, receipt with { AuthoredExpressionSha256 = null }),
            _ => throw new ArgumentOutOfRangeException(nameof(changedField)),
        };
        FbxModelAuthoringImportResult stale = MorphAuthoringEvidence.ReconcileTarget(changed);
        Assert.False(Assert.Single(stale.Package.Document.MorphAuthoringRecords).Accepted);

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string output = Path.Combine(directory, "blocked");
            var request = new Dl1SourceModelBuildRequest
            {
                Model = stale, OutputDirectory = output, ResourceName = "generic_face",
            };
            if (changedField == "slot")
            {
                ArgumentException failure = await Assert.ThrowsAsync<ArgumentException>(() => Dl1SourceModelWriter.WriteAsync(request));
                Assert.Contains("target channel mapping", failure.Message, StringComparison.Ordinal);
            }
            else
            {
                InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => Dl1SourceModelWriter.WriteAsync(request));
                Assert.Contains("Character expression export is blocked", failure.Message, StringComparison.Ordinal);
            }
            Assert.False(Directory.Exists(output));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ChangedNonzeroDeltasChannelSlotAndLegacyReceiptRequireFreshReview()
    {
        var accepted = AcceptedExpression();
        var face = Assert.Single(accepted.Surfaces);
        var expression = Assert.Single(face.MorphTargets);
        var receipt = Assert.Single(accepted.Package.Document.MorphAuthoringRecords);
        var changed = expression with { PositionDeltas = expression.PositionDeltas.SetItem(1, new(0.5, 0, 0)) };
        AssertStale(accepted with { Surfaces = [face with { MorphTargets = [changed] }] });
        AssertStale(WithReceipt(accepted, receipt with { TargetChannelSlot = 1 }));
        AssertStale(WithReceipt(accepted, receipt with { AuthoredExpressionSha256 = null }));
        var preserved = MorphAuthoringEvidence.ReconcileTarget(accepted with { Surfaces = [face with { MorphTargets = [changed] }] });
        Assert.Equal(receipt.AuthoredExpressionSha256, Assert.Single(preserved.Package.Document.MorphAuthoringRecords).AuthoredExpressionSha256);
        Assert.Equal(changed.PositionDeltas, Assert.Single(Assert.Single(preserved.Surfaces).MorphTargets).PositionDeltas);
        Assert.Equal(face.Vertices, Assert.Single(preserved.Surfaces).Vertices);
        Assert.Empty(MorphAuthoringEvidence.ExportBlockers(accepted));
    }
    private static FbxModelAuthoringImportResult AcceptedExpression(bool fractionalNeutral = false)
    {
        FbxModelAuthoringImportResult reference = FbxModelAuthoringImporter.ImportPackage(
            ModelsWorkspaceMorphAuthoringTests.CreateGenericReferencePackage());
        FbxModelAuthoringImportResult target = FbxModelAuthoringImporter.ImportPackage(
            ModelsWorkspaceMorphAuthoringTests.CreateGenericDifferentTopologyTargetPackage());
        if (fractionalNeutral)
        {
            FbxModelSurface originalFace = Assert.Single(target.Surfaces);
            target = target with { Surfaces = [originalFace with { Vertices = originalFace.Vertices.SetItem(0,
                originalFace.Vertices[0] with
                { Position = originalFace.Vertices[0].Position + new Vector3D(0.125, 0.25, 0) }) }] };
        }
        FbxModelSurface sourceFace = Assert.Single(reference.Surfaces);
        FbxModelSurface targetFace = Assert.Single(target.Surfaces);
        MorphReferenceProfile sourceProfile = CharacterGeometryAuthoring.CreateMorphProfile(reference, sourceFace.Id);
        MorphReferenceProfile targetProfile = CharacterGeometryAuthoring.CreateMorphProfile(target, targetFace.Id);
        ImmutableArray<Vector3D> authoredDeltas =
            [Vector3D.Zero, new(0.25, 0, 0), Vector3D.Zero, Vector3D.Zero];
        FbxModelAuthoringImportResult authored = CharacterGeometryAuthoring.SetExpression(target,
            targetFace.Id, Expression, Descriptor, authoredDeltas, MorphTransferConflict.Reject, reviewed: true);
        var receipt = new MorphAuthoringRecord
        {
            Name = Expression, DescriptorHash = Descriptor,
            ReferenceSourceSha256 = sourceProfile.SourceSha256,
            OriginalSourceChannelIndex = 5, TargetChannelSlot = 0,
            ReferenceSurfaceId = sourceFace.Id, TargetSurfaceId = targetFace.Id,
            ReferenceTopologyFingerprint = sourceProfile.TopologyFingerprint,
            TargetNeutralFingerprint = targetProfile.TopologyFingerprint,
            AuthoredExpressionSha256 = MorphAuthoringEvidence.ExpressionFingerprint(Assert.Single(Assert.Single(authored.Surfaces).MorphTargets)),
            Method = MorphAuthoringMethod.AssistedTransfer,
            ConflictChoice = MorphTransferConflict.Reject,
            ReviewedRegionName = "face",
            ReviewedTargetRegion = [0, 1, 2],
            ReviewedLandmarks = [new(sourceFace.Id, 1, targetFace.Id, 1)],
            ReviewedTriangles = [new(sourceFace.Id, 0, targetFace.Id, 0)],
            LockedTargetVertices = [2], Accepted = true,
        };
        FbxModelAuthoringImportResult withReceipt = authored with { Package = authored.Package with
        {
            Document = authored.Package.Document with { MorphAuthoringRecords = [receipt] },
        } };
        return ModelGeometryRevisionCodec.Capture(withReceipt);
    }

    private static FbxModelAuthoringImportResult WithReceipt(FbxModelAuthoringImportResult model,
        MorphAuthoringRecord receipt) => model with { Package = model.Package with
        { Document = model.Package.Document with { MorphAuthoringRecords = [receipt] } } };

    private static void AssertStale(FbxModelAuthoringImportResult model)
    {
        FbxModelAuthoringImportResult reconciled = MorphAuthoringEvidence.ReconcileTarget(model);
        MorphAuthoringRecord receipt = Assert.Single(reconciled.Package.Document.MorphAuthoringRecords);
        Assert.False(receipt.Accepted);
        Assert.Single(MorphAuthoringEvidence.ExportBlockers(reconciled));
    }
}

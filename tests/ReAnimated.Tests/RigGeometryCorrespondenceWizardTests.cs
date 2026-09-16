using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigGeometryCorrespondenceWizardTests
{
    [Fact]
    public void ActualCurrentSurfaceEvidenceReachesMappingReviewAndAcceptanceWithoutMutatingSource()
    {
        var (template, rig, geometry) = RigGeometryCorrespondenceTests.Fixture();
        var original = RigConformanceWizardTests.CreateModel();
        var vertices = ImmutableArray.CreateBuilder<FbxModelVertex>();
        foreach (var support in geometry.Supports)
        {
            Vector3D center = support.Centroid;
            foreach (var position in new[] { center, center + Vector3D.UnitX * .001, center + Vector3D.UnitY * .001 })
                vertices.Add(new(position, Vector3D.UnitZ, 0, 0, [support.BoneIndex], [1]));
        }
        var bones = rig.Bones.Select(b => new CustomModelBone { Index = b.Index, Name = b.Name, FbxObjectId = 1000 + b.Index,
            ParentIndex = b.ParentIndex, Kind = b.Kind, IsWeighted = geometry.Supports.Any(s => s.BoneIndex == b.Index),
            LocalBindTransform = b.LocalBindPose, ExactLocalBindMatrix = b.LocalBindPose.ToMatrix() }).ToImmutableArray();
        var document = original.Package.Document with { Bones = bones, RigSignature = CustomModelContractSignatures.ComputeRig(bones),
            Meshes = [new() { Name = "Body", ControlPointCount = vertices.Count, ExpandedVertexCount = vertices.Count,
                PolygonCount = vertices.Count / 3, TriangleCount = vertices.Count / 3, MaterialSlotCount = 1 }] };
        var surface = new FbxModelSurface("body", "Body", document.Materials[0].Id, vertices.ToImmutable(),
            Enumerable.Range(0, vertices.Count).Select(static i => (uint)i).ToImmutableArray(),
            Enumerable.Range(0, rig.BoneCount).ToImmutableArray(), geometry.GlobalBindMatrices.Select(static m => m.InvertedAffine()).ToImmutableArray(), true);
        var model = original with { Package = original.Package with { Document = document }, Rig = rig, Surfaces = [surface] };
        var wizard = new RigConformanceWizardViewModel((profile, _) => Task.FromResult(new Dl1RigTemplateResolution(
            template, profile, "synthetic", new string('b', 64), "synthetic geometry control")), static _ => { });
        wizard.SetModel(model);
        wizard.ResolveTemplateCommand.Execute(null);
        Assert.NotNull(wizard.Fit);
        Assert.True(wizard.HasPendingMappingReview);
        Assert.False(wizard.CanApply);
        Assert.True(wizard.AcceptMappingProposalsCommand.CanExecute(null));
        Assert.Contains(wizard.Mappings, r => r.Role == "body.head" && r.SourceName == "node_3" && r.CanChooseSource);
        Assert.Contains("Head", wizard.Mappings.Single(r => r.Name == "body.head").Candidates);
        wizard.AcceptMappingProposalsCommand.Execute(null);
        Assert.False(wizard.HasPendingMappingReview);
        Assert.True(wizard.CanApply);
        var settings = Assert.IsType<CustomModelRigConformance>(wizard.CreateSettings());
        Assert.Equal(template.EntityCount, settings.RoleOverrides.Length);
        Assert.Equal(CustomModelCorrespondenceMethod.GeometryHierarchyV1, settings.CorrespondenceMethod);
        Assert.Equal(0, settings.ConformanceStrength);
        Assert.Null(model.Package.Document.RigConformance);
        Assert.Same(surface, model.Surfaces[0]);
    }
}

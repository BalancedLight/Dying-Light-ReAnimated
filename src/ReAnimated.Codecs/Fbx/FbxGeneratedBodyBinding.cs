using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Codecs.Fbx;

public sealed record GeneratedBodyBindingWork(RiggingJobToken Token, string SourceSha256,
    ImmutableArray<SkinBindingHandle> Handles, ImmutableArray<SkinBindingPoint> Points,
    ImmutableArray<string> BoundComponentIds)
{
    public string? GridFingerprint { get; init; }
    public string? BindingInputFingerprint { get; init; }
    public SkinBindingOrigin Origin { get; init; }
}

/// <summary>Connects generated anatomy and candidate weights to the existing FBX surface/persistence contracts.</summary>
public static class FbxGeneratedBodyBinding
{
    public const string UnboundDiagnostic = "generated_body_binding_required";

    public static FbxModelAuthoringImportResult Generate(FbxModelAuthoringImportResult source, CancellationToken cancellationToken = default)
    {
        var generated = GeneratedBodyRig.Build(source.Package.Document);
        var document = source.Package.Document with { Bones = generated.Bones, RiggingSession = generated.Session,
            RigMode = CustomModelRigMode.ExactFbxRig, RigSignature = CustomModelContractSignatures.ComputeRig(generated.Bones),
            RigConformance = null, LastBuildReceipt = null,
            Diagnostics = source.Package.Document.Diagnostics.Where(d => d.Code != UnboundDiagnostic).Append(new CustomModelImportDiagnostic {
                Code = UnboundDiagnostic, Severity = CustomModelImportSeverity.Blocker,
                Message = "The generated body rig has no reviewed binding. Bind selected components and inspect deformation before exporting.",
            }).ToImmutableArray() };
        document.Validate();
        return FbxAuthoredModelLayer.Capture(source with { Package = source.Package with { Document = document }, Rig = document.CreateRigDefinition() }, cancellationToken);
    }

    public static GeneratedBodyBindingWork Prepare(FbxModelAuthoringImportResult model, CancellationToken cancellationToken = default)
    {
        if (!GeneratedBodyRig.IsGenerated(model.Package.Document)) throw new InvalidDataException("Automatic body binding requires this session's generated body rig.");
        var session = model.Package.Document.RiggingSession!;
        if (!session.MatchesSource(model.Package.Document.Source.ContentSha256)) throw new InvalidDataException("The source changed; reconcile it before binding.");
        var segments = GeneratedBodyRig.GetSegments(model.Package.Document);
        var handleByRole = segments.ToDictionary(static s => s.RoleId, StringComparer.Ordinal);
        var handles = segments.Select(static s => new SkinBindingHandle(s.EntityId, s.Start, s.End)).ToImmutableArray();
        var lockedPoints = session.WeightLocks.IsEmpty ? null : FbxSkinWeightAuthoring.Inspect(model, cancellationToken).Points
            .ToDictionary(static p => (p.ComponentId, p.ControlPointIndex));
        var source = FbxSourceGeometryAnalysis.Build(model, anatomyOnly: false, cancellationToken);
        var points = ImmutableArray.CreateBuilder<SkinBindingPoint>();
        var selected = ImmutableArray.CreateBuilder<string>();
        foreach (var component in source.Components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var choice = session.Components.Single(c => c.Id == component.Geometry.Id);
            if (choice.BindingMode == RigComponentBindingMode.KeepSource) continue;
            Guid? rigid = null;
            if (choice.BindingMode == RigComponentBindingMode.Rigid)
                rigid = choice.RigidBoneRoleId is { } role && handleByRole.TryGetValue(role, out var handle)
                    ? handle.EntityId : throw new InvalidDataException("The rigid component role has no generated deform influence.");
            var used = component.Triangles.SelectMany(static t => new[] { t.A, t.B, t.C }).Distinct().Order();
            foreach (int index in used)
            {
                ImmutableArray<FixedSkinInfluence> fixedWeights = rigid is { } id ? [new(id, 1)] : [];
                if (lockedPoints is not null && lockedPoints.TryGetValue((component.Geometry.Id, index), out var lockedPoint))
                {
                    var locks = lockedPoint.LockedInfluences.Select(locked => new FixedSkinInfluence(locked,
                        lockedPoint.Weights.Where(w => w.HandleId == locked).Sum(static w => w.Weight))).ToImmutableArray();
                    if (locks.Any(w => !handles.Any(h => h.Id == w.HandleId))) throw new InvalidDataException("A weight lock targets a bone outside this generated binding's deform handles.");
                    if (rigid is { } rigidId && locks.Any(w => w.Weight != (w.HandleId == rigidId ? 1 : 0)))
                        throw new InvalidDataException("Rigid reassignment conflicts with a saved weight lock. Unlock that influence before rebinding.");
                    fixedWeights = fixedWeights.AddRange(locks.Where(w => !fixedWeights.Any(f => f.HandleId == w.HandleId)));
                }
                points.Add(new(component.Geometry.Id, index, component.Geometry.ControlPoints[index]) { FixedInfluences = fixedWeights });
            }
            selected.Add(component.Geometry.Id);
        }
        if (points.Count == 0) throw new InvalidDataException("Choose automatic or rigid binding for at least one included component.");
        return new(session.CreateJobToken(), source.SourceSha256, handles, points.ToImmutable(), selected.ToImmutable());
    }

    public static GeneratedBodyBindingWork ForVolume(GeneratedBodyBindingWork work, SourceVolumeGrid grid, AutomaticSkinBindingOptions? options = null)
    {
        if (!string.Equals(grid.SourceSha256, work.SourceSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Binding volume belongs to another source.");
        return work with { GridFingerprint = grid.InputFingerprint, Origin = SkinBindingOrigin.VolumeGeodesic,
            BindingInputFingerprint = AutomaticSkinBinder.ComputeInputFingerprint(grid, work.Handles, work.Points, options) };
    }

    public static GeneratedBodyBindingWork ForFixed(GeneratedBodyBindingWork work) => work with {
        GridFingerprint = null, Origin = SkinBindingOrigin.ExplicitFixed,
        BindingInputFingerprint = AutomaticSkinBinder.ComputeFixedInputFingerprint(work.SourceSha256, work.Handles, work.Points),
    };

    public static bool TryApply(FbxModelAuthoringImportResult model, GeneratedBodyBindingWork work, AutomaticSkinBindingResult binding,
        out FbxModelAuthoringImportResult result, CancellationToken cancellationToken = default)
    {
        result = model;
        if (model.Package.Document.RiggingSession is not { } session || !session.Matches(work.Token) ||
            !string.Equals(work.SourceSha256, binding.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(work.SourceSha256, model.Package.Document.Source.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
            work.BindingInputFingerprint is null || binding.Origin != work.Origin ||
            !string.Equals(binding.InputFingerprint, work.BindingInputFingerprint, StringComparison.Ordinal) ||
            !string.Equals(binding.GridFingerprint, work.GridFingerprint, StringComparison.Ordinal)) return false;
        if (!binding.AllPointsAssigned) throw new InvalidDataException("Some selected points are unbound; review component assignment, rigid targets and volume coverage.");
        var expected = Prepare(model, cancellationToken);
        if (expected.Points.Length != work.Points.Length || expected.Points.Zip(work.Points).Any(p =>
                p.First.ComponentId != p.Second.ComponentId || p.First.ControlPointIndex != p.Second.ControlPointIndex || p.First.Position != p.Second.Position ||
                !p.First.FixedInfluences.SequenceEqual(p.Second.FixedInfluences) || !p.First.AllowedHandles.SequenceEqual(p.Second.AllowedHandles)) ||
            expected.Handles.Length != work.Handles.Length || expected.Handles.Zip(work.Handles).Any(h => h.First.Id != h.Second.Id ||
                h.First.Start != h.Second.Start || h.First.End != h.Second.End || !h.First.AllowedRegions.SequenceEqual(h.Second.AllowedRegions)) ||
            !expected.BoundComponentIds.SequenceEqual(work.BoundComponentIds))
            throw new InvalidDataException("The binding work no longer matches the validated source point and generated influence inventory.");
        var rows = binding.Points.ToDictionary(static p => (p.ComponentId, p.ControlPointIndex));
        if (rows.Count != work.Points.Length || work.Points.Any(p => !rows.ContainsKey((p.ComponentId, p.ControlPointIndex))))
            throw new InvalidDataException("Binding results must cover exactly the selected original source points.");
        var entityNames = session.Recipe.Entities.Where(e => e.OwnerAssetId == model.Package.Document.ModelId).ToDictionary(static e => e.EntityId, static e => e.NativeName);
        var boneByName = model.Package.Document.Bones.ToDictionary(static b => b.Name, static b => b.Index, StringComparer.Ordinal);
        var boneById = work.Handles.ToDictionary(static h => h.Id, h => boneByName[entityNames[h.Id]]);
        foreach (var point in work.Points)
        {
            var row = rows[(point.ComponentId, point.ControlPointIndex)];
            if (row.Influences.Length > 4 || row.Influences.Any(w => !boneById.ContainsKey(w.HandleId)))
                throw new InvalidDataException("Generated body output requires at most four declared deform influences per point.");
            foreach (var fixedWeight in point.FixedInfluences)
                if (row.Influences.Where(w => w.HandleId == fixedWeight.HandleId).Sum(static w => w.Weight) != fixedWeight.Weight)
                    throw new InvalidDataException("The binding result changed an explicit fixed assignment.");
        }
        var globals = new TransformMatrix[model.Package.Document.Bones.Length];
        foreach (var bone in model.Package.Document.Bones)
            globals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        var palette = Enumerable.Range(0, globals.Length).ToImmutableArray();
        var inverse = globals.Select(static g => g.InvertedAffine()).ToImmutableArray();
        var surfaces = ImmutableArray.CreateBuilder<FbxModelSurface>();
        foreach (var surface in model.Surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (surface.SourceGeometry is not { } source || !work.BoundComponentIds.Contains(source.Id)) { surfaces.Add(surface); continue; }
            var vertices = surface.Vertices.Select((v, i) => {
                var row = rows[(source.Id, surface.SourceCorners[i].ControlPointIndex)];
                return v with { BoneIndices = row.Influences.Select(w => boneById[w.HandleId]).ToImmutableArray(),
                    BoneWeights = row.Influences.Select(static w => w.Weight).ToImmutableArray() };
            }).ToImmutableArray();
            surfaces.AddRange(FbxAuthoredSurfacePartitioner.Partition(surface with {
                Vertices = vertices, PaletteBoneIndices = palette, InverseBindMatrices = inverse, IsSkinned = true,
            }, cancellationToken));
        }
        var updatedSession = RiggingSessions.Change(session, session with { BindingBackend = new() {
            Id = binding.Origin == SkinBindingOrigin.ExplicitFixed ? "explicit-fixed" : AutomaticSkinBindingResult.BackendId,
            Version = AutomaticSkinBindingResult.BackendVersion, SettingsSha256 = binding.InputFingerprint,
        } }, RiggingEditKind.Skinning);
        var diagnostics = model.Package.Document.Diagnostics.Where(d => d.Code != UnboundDiagnostic && d.Code != "generated_binding_review").Append(new CustomModelImportDiagnostic {
            Code = "generated_binding_review", Severity = CustomModelImportSeverity.Warning,
            Message = $"Generated weights assigned. Review deformation and {binding.Diagnostics.Length} binding diagnostic(s); runtime capabilities remain unverified.",
        }).ToImmutableArray();
        var document = model.Package.Document with { RiggingSession = updatedSession, Diagnostics = diagnostics, LastBuildReceipt = null };
        result = FbxAuthoredModelLayer.Capture(model with { Package = model.Package with { Document = document }, Surfaces = surfaces.ToImmutable() }, cancellationToken);
        return true;
    }
}

using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

/// <summary>
/// Controls for carrying an imported mesh into the conformed skeleton's rest
/// pose. This stage rewrites geometry, so its invariants matter more than most:
/// a mistake here mangles the model rather than merely misplacing a bone.
/// </summary>
public sealed class Dl1RestPoseBakerTests
{
    [Fact]
    public void MorphTargetNormalMovesWithTheRebasedRestPose()
    {
        RigDefinition rig=CreateRig();var globals=rig.CreateBindPose().GlobalMatrices;
        var targets=globals.Select(static global=>(Vector3D?)global.Translation).ToImmutableArray();
        int hand=rig.GetBoneIndex("hand"),elbow=rig.GetBoneIndex("forearm");
        Vector3D offset=globals[hand].Translation-globals[elbow].Translation;
        targets=targets.SetItem(hand,globals[elbow].Translation+new Vector3D(0,offset.X,0));
        var transfer=RigRestPoseTransfer.Solve(rig,globals,targets);
        FbxModelSurface original=CreateSurface(rig);
        FbxModelSurface withMorph=original with { MorphTargets=[new FbxModelMorphTarget("normal_turn",1,2,3,
            Enumerable.Repeat(Vector3D.Zero,original.Vertices.Length).ToImmutableArray())
            {NormalDeltas=original.Vertices.Select(v=>Vector3D.UnitX-v.Normal).ToImmutableArray()}] };
        var baked=Assert.Single(Dl1RestPoseBaker.Bake([withMorph],transfer).Surfaces);
        var targetOnly=original with {Vertices=original.Vertices.Select(v=>v with {Normal=Vector3D.UnitX}).ToImmutableArray()};
        var expected=Assert.Single(Dl1RestPoseBaker.Bake([targetOnly],transfer).Surfaces);
        var deltas=Assert.Single(baked.MorphTargets).NormalDeltas;
        for(int i=0;i<deltas.Length;i++)Assert.True((baked.Vertices[i].Normal+deltas[i]-expected.Vertices[i].Normal).Length<1e-9);
    }

    [Fact]
    public void AnUnchangedRestPoseLeavesEveryVertexWhereItWas()
    {
        RigDefinition rig = CreateRig();
        ImmutableArray<TransformMatrix> globals = rig.CreateBindPose().GlobalMatrices;
        RigRestPoseTransferResult transfer = RigRestPoseTransfer.Solve(
            rig,
            globals,
            globals.Select(static global => (Vector3D?)global.Translation).ToImmutableArray());
        FbxModelSurface surface = CreateSurface(rig);

        Dl1RestPoseBakeResult baked = Dl1RestPoseBaker.Bake([surface], transfer);

        FbxModelSurface result = Assert.Single(baked.Surfaces);
        for (int index = 0; index < surface.Vertices.Length; index++)
        {
            Assert.Equal(
                0.0,
                Vector3D.Distance(
                    surface.Vertices[index].Position,
                    result.Vertices[index].Position),
                9);
        }

        Assert.Equal(0.0, baked.Report.MaximumVertexDisplacement, 9);
        Assert.Equal(0, baked.Report.UnweightedVertices);
    }

    [Fact]
    public void RotatingABoneCarriesItsVerticesRigidly()
    {
        RigDefinition rig = CreateRig();
        ImmutableArray<TransformMatrix> globals = rig.CreateBindPose().GlobalMatrices;

        // Swing the hand a quarter turn about the elbow, keeping its distance
        // from that joint. A rotation must carry the vertices rigidly; asking
        // for a different distance would be a stretch, which is a separate
        // thing and not what this control measures.
        var targets = globals
            .Select(static global => (Vector3D?)global.Translation)
            .ToImmutableArray();
        int hand = rig.GetBoneIndex("hand");
        int elbow = rig.GetBoneIndex("forearm");
        Vector3D bindOffset =
            globals[hand].Translation - globals[elbow].Translation;
        Vector3D swung = new(0.0, bindOffset.X, 0.0);
        targets = targets.SetItem(hand, globals[elbow].Translation + swung);

        RigRestPoseTransferResult transfer =
            RigRestPoseTransfer.Solve(rig, globals, targets);
        FbxModelSurface surface = CreateSurface(rig);
        Dl1RestPoseBakeResult baked = Dl1RestPoseBaker.Bake([surface], transfer);

        Assert.Equal(0.0, transfer.MaximumJointResidual, 9);
        Assert.True(baked.Report.MaximumVertexDisplacement > 0.0);

        // A vertex bound entirely to the hand keeps its distance from the
        // elbow it swung around.
        double before = Vector3D.Distance(
            surface.Vertices[2].Position,
            globals[elbow].Translation);
        double after = Vector3D.Distance(
            Assert.Single(baked.Surfaces).Vertices[2].Position,
            transfer.PosedGlobals[elbow].Translation);

        // A tenth of a millimetre. The elbow has a single constraint, so its
        // twist is settled by a small bias toward the parent rather than by the
        // data, which leaves a rotation error under a thousandth of a degree.
        Assert.Equal(before, after, 4);
    }

    [Fact]
    public void EveryTransferTransformIsARotationAndTranslationOnly()
    {
        RigDefinition rig = CreateRig();
        ImmutableArray<TransformMatrix> globals = rig.CreateBindPose().GlobalMatrices;
        int hand = rig.GetBoneIndex("hand");
        ImmutableArray<Vector3D?> targets = globals
            .Select(static global => (Vector3D?)global.Translation)
            .ToImmutableArray()
            .SetItem(hand, new Vector3D(0.1, 1.4, 0.3));

        RigRestPoseTransferResult transfer =
            RigRestPoseTransfer.Solve(rig, globals, targets);

        Assert.All(
            transfer.SkinningTransforms,
            static transform =>
            {
                Assert.True(transform.IsFinite);
                Assert.Equal(1.0, transform.LinearDeterminant, 6);
            });
    }

    [Fact]
    public void NormalsStayUnitLength()
    {
        RigDefinition rig = CreateRig();
        ImmutableArray<TransformMatrix> globals = rig.CreateBindPose().GlobalMatrices;
        int hand = rig.GetBoneIndex("hand");
        RigRestPoseTransferResult transfer = RigRestPoseTransfer.Solve(
            rig,
            globals,
            globals.Select(static g => (Vector3D?)g.Translation).ToImmutableArray()
                .SetItem(hand, new Vector3D(0.2, 1.5, 0.2)));

        Dl1RestPoseBakeResult baked =
            Dl1RestPoseBaker.Bake([CreateSurface(rig)], transfer);

        Assert.All(
            Assert.Single(baked.Surfaces).Vertices,
            static vertex => Assert.Equal(1.0, vertex.Normal.Length, 6));
    }

    [Fact]
    public void UnskinnedSurfacesPassThroughUntouched()
    {
        RigDefinition rig = CreateRig();
        ImmutableArray<TransformMatrix> globals = rig.CreateBindPose().GlobalMatrices;
        RigRestPoseTransferResult transfer = RigRestPoseTransfer.Solve(
            rig,
            globals,
            globals.Select(static g => (Vector3D?)g.Translation).ToImmutableArray());
        FbxModelSurface staticSurface = CreateSurface(rig) with
        {
            IsSkinned = false,
            PaletteBoneIndices = [],
        };

        Dl1RestPoseBakeResult baked =
            Dl1RestPoseBaker.Bake([staticSurface], transfer);

        Assert.Same(staticSurface, Assert.Single(baked.Surfaces));
    }

    [Fact]
    public void UnknownSourceBoneInAPaletteFailsClosed()
    {
        RigDefinition rig = CreateRig();
        ImmutableArray<TransformMatrix> globals = rig.CreateBindPose().GlobalMatrices;
        RigRestPoseTransferResult transfer = RigRestPoseTransfer.Solve(
            rig,
            globals,
            globals.Select(static g => (Vector3D?)g.Translation).ToImmutableArray());
        FbxModelSurface broken = CreateSurface(rig) with
        {
            PaletteBoneIndices = [rig.BoneCount + 3],
        };

        Assert.Throws<InvalidDataException>(
            () => Dl1RestPoseBaker.Bake([broken], transfer));
    }

    /// <summary>
    /// A bone's rotation must be solved from every joint hanging off it, not
    /// one chosen child. Aiming at a single child makes the others inherit its
    /// correction, which is how a small leg error becomes a large torso one.
    /// </summary>
    [Fact]
    public void MovingOneLimbLeavesTheOtherAlone()
    {
        RigDefinition rig = CreateRig();
        ImmutableArray<TransformMatrix> globals = rig.CreateBindPose().GlobalMatrices;
        int hand = rig.GetBoneIndex("hand");
        int foot = rig.GetBoneIndex("foot");

        RigRestPoseTransferResult transfer = RigRestPoseTransfer.Solve(
            rig,
            globals,
            globals.Select(static g => (Vector3D?)g.Translation).ToImmutableArray()
                .SetItem(hand, globals[hand].Translation + new Vector3D(0.0, 0.3, 0.0)));

        Assert.Equal(
            0.0,
            Vector3D.Distance(
                globals[foot].Translation,
                transfer.PosedGlobals[foot].Translation),
            6);
    }

    private static RigDefinition CreateRig()
    {
        var rows = new List<(string Name, int Parent, Vector3D Offset)>
        {
            ("root", -1, Vector3D.Zero),
            ("pelvis", 0, new Vector3D(0.0, 0.95, 0.0)),
            ("spine", 1, new Vector3D(0.0, 0.20, 0.0)),
            ("upperarm", 2, new Vector3D(0.18, 0.20, 0.0)),
            ("forearm", 3, new Vector3D(0.28, 0.0, 0.0)),
            ("hand", 4, new Vector3D(0.24, 0.0, 0.0)),
            ("thigh", 1, new Vector3D(0.10, -0.05, 0.0)),
            ("calf", 6, new Vector3D(0.0, -0.42, 0.0)),
            ("foot", 7, new Vector3D(0.0, -0.43, 0.0)),
        };

        var bones = ImmutableArray.CreateBuilder<BoneDefinition>(rows.Count);
        for (int index = 0; index < rows.Count; index++)
        {
            (string name, int parent, Vector3D offset) = rows[index];
            bones.Add(new BoneDefinition(
                index,
                name,
                parent,
                new TransformTRS(offset, QuaternionD.Identity, Vector3D.One),
                parent < 0 ? BoneKind.Root : BoneKind.Deform));
        }

        return new RigDefinition("source:bake", "bake", bones.MoveToImmutable());
    }

    private static FbxModelSurface CreateSurface(RigDefinition rig)
    {
        ImmutableArray<TransformMatrix> globals = rig.CreateBindPose().GlobalMatrices;
        int forearm = rig.GetBoneIndex("forearm");
        int hand = rig.GetBoneIndex("hand");
        int spine = rig.GetBoneIndex("spine");

        return new FbxModelSurface(
            "body",
            "Body",
            Guid.NewGuid(),
            [
                Vertex(globals[spine].Translation + new Vector3D(0.0, 0.02, 0.05), [0], [1.0]),
                Vertex(globals[forearm].Translation + new Vector3D(0.03, 0.02, 0.0), [1], [1.0]),
                Vertex(globals[hand].Translation + new Vector3D(0.02, 0.0, 0.01), [2], [1.0]),
                Vertex(
                    globals[forearm].Translation + new Vector3D(0.10, 0.01, 0.0),
                    [1, 2],
                    [0.5, 0.5]),
            ],
            [0, 1, 2],
            [spine, forearm, hand],
            [TransformMatrix.Identity, TransformMatrix.Identity, TransformMatrix.Identity],
            IsSkinned: true);
    }

    private static FbxModelVertex Vertex(
        Vector3D position,
        ImmutableArray<int> indices,
        ImmutableArray<double> weights) =>
        new(position, Vector3D.UnitY, 0.0, 0.0, indices, weights);
}

using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class AuthoredModelLayerCodecTests
{
    [Fact]
    public void RoundTripsAllSourceLinkedEditKindsAndCanonicalizesOrdering()
    {
        Guid firstBone = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid secondBone = Guid.Parse("00000000-0000-0000-0000-000000000002");
        TransformMatrix inverseBind = new(
            1.0, 0.25, 0.0, 2.0,
            0.0, 2.0, 0.1, -1.0,
            0.0, 0.0, 0.5, 3.0,
            0.0, 0.0, 0.0, 1.0);
        var layer = new AuthoredModelLayer
        {
            SourceSha256 = Hash('a'),
            SourceGeometryFingerprint = Hash('b'),
            TargetRigSignature = Hash('c'),
            Bones = [new AuthoredBoneIdentity(secondBone, "arm"), new AuthoredBoneIdentity(firstBone, "root")],
            Components =
            [
                new AuthoredComponentEdits
                {
                    ComponentId = "body",
                    ControlPointCount = 4,
                    PolygonVertexCount = 6,
                    InverseBinds = [new AuthoredInverseBind(secondBone, inverseBind)],
                    Points =
                    [
                        new AuthoredPointEdit { ControlPointIndex = 3, PositionDelta = new(0, 0, 0), ReplaceWeights = true, Weights = [] },
                        new AuthoredPointEdit
                        {
                            ControlPointIndex = 1,
                            PositionDelta = new(1, 2, 3),
                            ReplaceWeights = true,
                            Weights = [new AuthoredSkinInfluence(secondBone, 0.25), new AuthoredSkinInfluence(firstBone, 0.75)],
                        },
                    ],
                    Normals = [new AuthoredVectorDelta(4, new(0, 1, 0))],
                    Morphs =
                    [
                        new AuthoredMorphEdits
                        {
                            DescriptorHash = 7,
                            PositionDeltas = [new AuthoredVectorDelta(1, new(.1, 0, 0))],
                            NormalDeltas = [new AuthoredVectorDelta(1, new(0, .2, 0))],
                            HasNormalDeltas = true,
                        },
                        new AuthoredMorphEdits
                        {
                            DescriptorHash = 8,
                            HasNormalDeltas = true,
                        },
                    ],
                },
            ],
        };

        ImmutableArray<byte> first = AuthoredModelLayerCodec.Serialize(layer);
        ImmutableArray<byte> second = AuthoredModelLayerCodec.Serialize(layer with
        {
            Bones = layer.Bones.Reverse().ToImmutableArray(),
            Components = layer.Components.Select(component => component with
            {
                Points = component.Points.Reverse().ToImmutableArray(),
            }).ToImmutableArray(),
        });
        Assert.Equal<byte>(first, second);

        AuthoredModelLayer reopened = AuthoredModelLayerCodec.Deserialize(first.AsSpan());
        Assert.Equal(layer.SourceSha256, reopened.SourceSha256);
        Assert.Equal(2, reopened.Bones.Length);
        Assert.Equal(2, reopened.Components[0].Points.Length);
        Assert.True(reopened.Components[0].Points[0].ReplaceWeights);
        Assert.Equal(1, reopened.Components[0].Points[0].ControlPointIndex);
        Assert.Equal(new Vector3D(1, 2, 3), reopened.Components[0].Points[0].PositionDelta);
        Assert.Equal(2, reopened.Components[0].Points[0].Weights.Length);
        Assert.Equal(3, reopened.Components[0].Points[1].ControlPointIndex);
        Assert.Empty(reopened.Components[0].Points[1].Weights);
        Assert.True(reopened.Components[0].Morphs[0].HasNormalDeltas is true);
        Assert.True(reopened.Components[0].Morphs[1].HasNormalDeltas is true);
        Assert.Empty(reopened.Components[0].Morphs[1].NormalDeltas);
        Assert.Equal(inverseBind, reopened.Components[0].InverseBinds[0].Matrix);
    }

    [Fact]
    public void RejectsInvalidReferencesSumsDuplicatesAndNonFiniteValues()
    {
        Guid bone = Guid.NewGuid();
        AuthoredModelLayer valid = Layer(bone);
        Assert.Throws<ArgumentException>(() => (valid with { Bones = [new AuthoredBoneIdentity(bone, "root"), new AuthoredBoneIdentity(bone, "other")] }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with
        {
            Components = [valid.Components[0] with
            {
                Points = [valid.Components[0].Points[0] with { Weights = [new AuthoredSkinInfluence(Guid.NewGuid(), 1)] }],
            }],
        }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with
        {
            Components = [valid.Components[0] with
            {
                InverseBinds = [new AuthoredInverseBind(Guid.NewGuid(), TransformMatrix.Identity)],
            }],
        }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with
        {
            Components = [valid.Components[0] with
            {
                InverseBinds = [new AuthoredInverseBind(bone, new TransformMatrix(
                    1, 0, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 1, 0,
                    0, 0, 0, 1))],
            }],
        }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with
        {
            Components = [valid.Components[0] with
            {
                Morphs = [new AuthoredMorphEdits
                {
                    DescriptorHash = 1,
                    HasNormalDeltas = false,
                    NormalDeltas = [new AuthoredVectorDelta(0, Vector3D.UnitX)],
                }],
            }],
        }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with
        {
            Components = [valid.Components[0] with
            {
                Points = [valid.Components[0].Points[0] with { Weights = [new AuthoredSkinInfluence(bone, .5), new AuthoredSkinInfluence(bone, .5)] }],
            }],
        }).Validate());
        Assert.Throws<ArgumentException>(() => (valid with
        {
            Components = [valid.Components[0] with
            {
                Points = [valid.Components[0].Points[0] with { PositionDelta = new(double.NaN, 0, 0) }],
            }],
        }).Validate());
    }

    [Fact]
    public void RoundTripsSeveralEmptyWeightOverridesShortNamesAndPresenceOnlyNormals()
    {
        Guid bone = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var layer = new AuthoredModelLayer
        {
            SourceSha256 = Hash('a'),
            SourceGeometryFingerprint = Hash('b'),
            TargetRigSignature = Hash('c'),
            Bones = [new AuthoredBoneIdentity(bone, "x")],
            Components =
            [
                new AuthoredComponentEdits
                {
                    ComponentId = "c",
                    ControlPointCount = 3,
                    PolygonVertexCount = 9,
                    Points =
                    [
                        new AuthoredPointEdit(0, Vector3D.Zero, true, []),
                        new AuthoredPointEdit(1, Vector3D.Zero, true, []),
                        new AuthoredPointEdit(2, Vector3D.Zero, true, []),
                    ],
                    Morphs =
                    [
                        new AuthoredMorphEdits(11, [], [], true),
                    ],
                },
            ],
        };

        AuthoredModelLayer reopened = AuthoredModelLayerCodec.Deserialize(
            AuthoredModelLayerCodec.Serialize(layer).AsSpan());
        Assert.Equal(3, reopened.Components[0].Points.Length);
        Assert.All(reopened.Components[0].Points, point =>
        {
            Assert.True(point.ReplaceWeights);
            Assert.Empty(point.Weights);
        });
        Assert.True(reopened.Components[0].Morphs[0].HasNormalDeltas is true);
        Assert.Empty(reopened.Components[0].Morphs[0].NormalDeltas);
    }

    [Fact]
    public void ValidatesMorphNormalDeltasAgainstPolygonVertexIdentity()
    {
        Guid bone = Guid.NewGuid();
        AuthoredModelLayer valid = Layer(bone) with
        {
            Components = [Layer(bone).Components[0] with
            {
                PolygonVertexCount = 9,
                Morphs = [new AuthoredMorphEdits(
                    3,
                    [],
                    [new AuthoredVectorDelta(8, Vector3D.UnitY)])],
            }],
        };
        valid.Validate();

        AuthoredModelLayer invalid = valid with
        {
            Components = [valid.Components[0] with
            {
                Morphs = [valid.Components[0].Morphs[0] with
                {
                    NormalDeltas = [new AuthoredVectorDelta(9, Vector3D.UnitY)],
                }],
            }],
        };
        Assert.Throws<ArgumentException>(() => invalid.Validate());
    }

    [Fact]
    public void RejectsMalformedPayloadsBeforeTrustingUnknownOrHostileStructure()
    {
        ImmutableArray<byte> payload = AuthoredModelLayerCodec.Serialize(Layer(Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() => AuthoredModelLayerCodec.Deserialize(payload.AsSpan(0, payload.Length - 1)));
        Assert.Throws<InvalidDataException>(() => AuthoredModelLayerCodec.Deserialize(payload.Add(0).AsSpan()));

        byte[] unknownVersion = payload.ToArray();
        unknownVersion[4] = 2;
        Assert.Throws<InvalidDataException>(() => AuthoredModelLayerCodec.Deserialize(unknownVersion));
        byte[] hostileCount = payload.ToArray();
        // The first count after the three fixed strings is the bone count.
        int boneCountOffset = 8 + 4 + 64 + 4 + 64 + 4 + 64;
        BitConverter.TryWriteBytes(hostileCount.AsSpan(boneCountOffset, 4), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => AuthoredModelLayerCodec.Deserialize(hostileCount));
    }

    [Fact]
    public void RejectsNonFiniteBinaryDoubleAndPreservesExactDoubleValues()
    {
        Guid bone = Guid.NewGuid();
        AuthoredModelLayer layer = Layer(bone) with
        {
            Components = [Layer(bone).Components[0] with
            {
                Points = [Layer(bone).Components[0].Points[0] with { PositionDelta = new(0.125, -0.25, 0.5) }],
            }],
        };
        ImmutableArray<byte> payload = AuthoredModelLayerCodec.Serialize(layer);
        AuthoredModelLayer reopened = AuthoredModelLayerCodec.Deserialize(payload.AsSpan());
        Assert.Equal(0.125, reopened.Components[0].Points[0].PositionDelta.X);
        Assert.Equal(-0.25, reopened.Components[0].Points[0].PositionDelta.Y);
        Assert.Equal(0.5, reopened.Components[0].Points[0].PositionDelta.Z);

        byte[] nonFinite = payload.ToArray();
        // Header, source hashes, one bone, component metadata, counts, and point index.
        const int positionDeltaXOffset = 272;
        BitConverter.TryWriteBytes(nonFinite.AsSpan(positionDeltaXOffset, 8), double.PositiveInfinity);
        Assert.Throws<InvalidDataException>(() => AuthoredModelLayerCodec.Deserialize(nonFinite));
    }

    private static AuthoredModelLayer Layer(Guid bone)
    {
        return new AuthoredModelLayer
        {
            SourceSha256 = Hash('a'),
            SourceGeometryFingerprint = Hash('b'),
            TargetRigSignature = Hash('c'),
            Bones = [new AuthoredBoneIdentity(bone, "root")],
            Components =
            [
                new AuthoredComponentEdits
                {
                    ComponentId = "body",
                    ControlPointCount = 1,
                    PolygonVertexCount = 1,
                    Points =
                    [
                        new AuthoredPointEdit
                        {
                            ControlPointIndex = 0,
                            ReplaceWeights = true,
                            Weights = [new AuthoredSkinInfluence(bone, 1)],
                        },
                    ],
                },
            ],
        };
    }

    private static string Hash(char value) => new(value, 64);
}

using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class DerivedAnimationPackageTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void DerivedPayloadRoundTripsDeterministicallyAndPreservesSourceBytes()
    {
        CustomModelPackage package = Package(out CustomModelAnimationClip clip, out ImmutableArray<byte> payload);
        ImmutableArray<byte> first = CustomModelPackageSerializer.Serialize(package);
        ImmutableArray<byte> second = CustomModelPackageSerializer.Serialize(package);
        Assert.True(first.AsSpan().SequenceEqual(second.AsSpan()));

        string path = Path.Combine(_directory, "derived.dlrmodel");
        File.WriteAllBytes(path, first.ToArray());
        CustomModelPackage loaded = CustomModelPackageSerializer.Load(path);
        Assert.True(package.SourceFbx.AsSpan().SequenceEqual(loaded.SourceFbx.AsSpan()));
        Assert.True(payload.AsSpan().SequenceEqual(loaded.DerivedAnimationPayloads[clip.Id].AsSpan()));
        DerivedAnimationData data = DerivedAnimationDataCodec.Deserialize(loaded.DerivedAnimationPayloads[clip.Id].AsSpan());
        Assert.Equal(clip.Id, data.ClipId);
        Assert.Equal(2, data.TransformTracks.Length);
        Assert.Equal("arm", data.TransformTracks[0].BoneName);
        Assert.Equal("head", data.TransformTracks[1].BoneName);
    }

    [Fact]
    public void DerivedDataCanonicalizesTrackOrderButRejectsUnorderedKeys()
    {
        DerivedAnimationData data = Data(Guid.Parse("40000000-0000-0000-0000-000000000001"));
        ImmutableArray<byte> canonical = DerivedAnimationDataCodec.Serialize(data with
        {
            TransformTracks = data.TransformTracks.Reverse().ToImmutableArray(),
            ScalarTracks = data.ScalarTracks.Reverse().ToImmutableArray(),
        });
        Assert.True(canonical.AsSpan().SequenceEqual(DerivedAnimationDataCodec.Serialize(data).AsSpan()));
        Assert.Throws<ArgumentException>(() => DerivedAnimationDataCodec.Serialize(data with
        {
            TransformTracks = [new DerivedTransformTrack("arm", [new TransformKeyframe(1, TransformTRS.Identity), new TransformKeyframe(0, TransformTRS.Identity)])],
        }));
    }

    [Fact]
    public void MissingOrTamperedPayloadsAreRejectedBeforeLoadCompletes()
    {
        CustomModelPackage package = Package(out _, out _);
        Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.Serialize(
            package with { DerivedAnimationPayloads = ImmutableDictionary<Guid, ImmutableArray<byte>>.Empty }));

        ImmutableArray<byte> tampered = package.DerivedAnimationPayloads.Values.Single().SetItem(0,
            (byte)(package.DerivedAnimationPayloads.Values.Single()[0] ^ 0xff));
        Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.Serialize(
            package with { DerivedAnimationPayloads = package.DerivedAnimationPayloads.SetItem(package.Document.AnimationClips[0].Id, tampered) }));

        ImmutableArray<byte> orphan = AddZipEntry(CustomModelPackageSerializer.Serialize(package),
            "animation/derived/50000000000000000000000000000005.json", [1, 2, 3]);
        string path = Path.Combine(_directory, "orphan.dlrmodel");
        File.WriteAllBytes(path, orphan.ToArray());
        Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.Load(path));
    }

    [Fact]
    public void StaleTargetSignatureRemainsInspectable()
    {
        CustomModelPackage package = Package(out CustomModelAnimationClip clip, out _);
        string stale = new string('f', 64);
        CustomModelAnimationClip staleClip = clip with { DerivedMotion = clip.DerivedMotion! with { TargetRigSignature = stale } };
        CustomModelPackage stalePackage = package with
        {
            Document = package.Document with { AnimationClips = [staleClip] },
        };
        string path = Path.Combine(_directory, "stale.dlrmodel");
        File.WriteAllBytes(path, CustomModelPackageSerializer.Serialize(stalePackage).ToArray());
        CustomModelPackage loaded = CustomModelPackageSerializer.Load(path);
        Assert.Equal(stale, loaded.Document.AnimationClips[0].DerivedMotion!.TargetRigSignature);
        Assert.True(loaded.DerivedAnimationPayloads.ContainsKey(clip.Id));
    }

    [Fact]
    public void RejectsInvalidDerivedTrackStateAndReferenceMeasurements()
    {
        DerivedAnimationData data = Data(Guid.Parse("40000000-0000-0000-0000-000000000002"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DerivedAnimationDataCodec.Serialize(data with { FrameCount = 0 }));
        Assert.Throws<ArgumentException>(() => DerivedAnimationDataCodec.Serialize(data with
        {
            AuxiliaryTracks = [new DerivedAuxiliaryTrack(1, [new TransformKeyframe(0, new TransformTRS(Vector3D.Zero, QuaternionD.Identity, new(0, 1, 1)))])],
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DerivedMotionReference
        {
            SourceClipId = data.ClipId,
            SourceClipFingerprint = Hash('a'),
            SourceFileSha256 = Hash('b'),
            SourceRigSignature = Hash('c'),
            TargetRigSignature = Hash('d'),
            AlgorithmId = "generic",
            SampleMultiplier = 9,
            PayloadSha256 = Hash('e'),
            PayloadLength = 1,
        }.Validate());
    }

    private static CustomModelPackage Package(out CustomModelAnimationClip clip, out ImmutableArray<byte> payload)
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "derived-animation.fbx");
        CustomModelDocument source = imported.Package.Document;
        clip = new CustomModelAnimationClip
        {
            Id = Guid.Parse("40000000-0000-0000-0000-000000000010"),
            FbxObjectId = 1,
            SourceName = "source_clip",
            DisplayName = "Source Clip",
            FrameRate = new FrameRate(30, 1),
            FrameCount = 3,
            SourceFingerprint = source.Source.ContentSha256,
            HasSkeletalTracks = true,
        };
        DerivedAnimationData data = Data(clip.Id);
        payload = DerivedAnimationDataCodec.Serialize(data);
        clip = clip with
        {
            DerivedMotion = new DerivedMotionReference
            {
                SourceClipId = Guid.Parse("40000000-0000-0000-0000-000000000011"),
                SourceClipFingerprint = clip.SourceFingerprint,
                SourceFileSha256 = source.Source.ContentSha256,
                SourceRigSignature = source.RigSignature,
                TargetRigSignature = source.RigSignature,
                AlgorithmId = "generic-test-solver",
                SampleMultiplier = 2,
                PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan())),
                PayloadLength = payload.Length,
                MaximumPositionError = .001,
                MaximumAngularError = .002,
                MaximumLinearError = .003,
            },
        };
        CustomModelDocument document = source with { AnimationClips = [clip] };
        return imported.Package with
        {
            Document = document,
            DerivedAnimationPayloads = ImmutableDictionary<Guid, ImmutableArray<byte>>.Empty.Add(clip.Id, payload),
        };
    }

    private static DerivedAnimationData Data(Guid clipId) => new()
    {
        ClipId = clipId,
        Name = "Derived source clip",
        FrameRate = new FrameRate(30, 1),
        FrameCount = 3,
        TransformTracks =
        [
            new DerivedTransformTrack("head", [new TransformKeyframe(0, TransformTRS.Identity), new TransformKeyframe(2,
                new TransformTRS(new(0, .1, 0), QuaternionD.Identity, Vector3D.One))]),
            new DerivedTransformTrack("arm", [new TransformKeyframe(0, TransformTRS.Identity), new TransformKeyframe(2, TransformTRS.Identity)]),
        ],
        ScalarTracks = [new DerivedScalarTrack("smile", [new ScalarKeyframe(0, 0), new ScalarKeyframe(2, 1)])],
        AuxiliaryTracks = [new DerivedAuxiliaryTrack(17, [new TransformKeyframe(0, TransformTRS.Identity), new TransformKeyframe(2, TransformTRS.Identity)])],
    };

    private static ImmutableArray<byte> AddZipEntry(ImmutableArray<byte> original, string name, ImmutableArray<byte> bytes)
    {
        using var output = new MemoryStream();
        using (var destination = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var input = new ZipArchive(new MemoryStream(original.ToArray()), ZipArchiveMode.Read, leaveOpen: false))
        {
            foreach (ZipArchiveEntry entry in input.Entries)
            {
                ZipArchiveEntry copy = destination.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                using Stream source = entry.Open();
                using Stream target = copy.Open();
                source.CopyTo(target);
            }
            ZipArchiveEntry orphan = destination.CreateEntry(name, CompressionLevel.NoCompression);
            using Stream targetOrphan = orphan.Open();
            targetOrphan.Write(bytes.AsSpan());
        }
        return ImmutableArray.Create(output.ToArray());
    }

    private static string Hash(char value) => new(value, 64);
    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(_directory);
}

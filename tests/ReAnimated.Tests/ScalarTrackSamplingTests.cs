using ReAnimated.Core.Domain;

namespace ReAnimated.Tests;

public sealed class ScalarTrackSamplingTests
{
    [Fact]
    [Trait("Category", "Stability")]
    public void DenseLongTrackCanBeSampledAcrossEntireRange()
    {
        const int frameCount = 32_768;
        var keys = new ScalarKeyframe[frameCount];
        for (int frame = 0; frame < keys.Length; frame++)
        {
            keys[frame] = new ScalarKeyframe(
                frame,
                frame * 0.25);
        }

        var track = new ScalarTrack("morph_dense", keys);
        double checksum = 0;
        for (int frame = 0; frame < frameCount - 1; frame++)
        {
            checksum += track.Sample(frame + 0.5);
        }

        double sampleCount = frameCount - 1;
        double expected =
            0.25 * sampleCount * sampleCount / 2.0;
        Assert.Equal(expected, checksum, 8);
        Assert.Equal(
            (frameCount - 1) * 0.25,
            track.Sample(frameCount + 100),
            12);
    }
}

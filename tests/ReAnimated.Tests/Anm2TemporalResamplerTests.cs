using System.Collections.Immutable;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class Anm2TemporalResamplerTests
{
    [Fact]
    public void Frames381At30ResampleTo305At24WithExactEndpoints()
    {
        ImmutableArray<ImmutableArray<TransformTRS>> source =
            CreateFrames(381);

        Anm2TemporalResampleResult result =
            Anm2TemporalResampler.Resample(
                source,
                inputFramesPerSecond: 30.0,
                outputFramesPerSecond: 24.0);

        Assert.Equal(305, result.FrameCount);
        Assert.Equal(24.0, result.FbxOutputFps);
        Assert.Equal(30.0, result.Anm2InputFps);
        Assert.Equal(
            source[0][0].Translation,
            result.Frames[0][0].Translation);
        Assert.Equal(
            source[^1][0].Translation,
            result.Frames[^1][0].Translation);
        Assert.Equal(
            source[0][0].Scale,
            result.Frames[0][0].Scale);
        Assert.Equal(
            source[^1][0].Scale,
            result.Frames[^1][0].Scale);
        Assert.InRange(
            Math.Abs(QuaternionD.Dot(
                source[0][0].Rotation,
                result.Frames[0][0].Rotation)),
            1.0 - 1.0e-12,
            1.0 + 1.0e-12);
        Assert.InRange(
            Math.Abs(QuaternionD.Dot(
                source[^1][0].Rotation,
                result.Frames[^1][0].Rotation)),
            1.0 - 1.0e-12,
            1.0 + 1.0e-12);
        for (int frame = 1;
             frame < result.FrameCount;
             frame++)
        {
            Assert.True(
                QuaternionD.Dot(
                    result.Frames[frame - 1][0].Rotation,
                    result.Frames[frame][0].Rotation) >=
                0.0);
        }

        Anm2TemporalResamplePlan plan =
            Anm2TemporalResampler.CreatePlan(
                381,
                30.0,
                24.0);
        Assert.Equal(305, plan.OutputFrameCount);
        Assert.Equal(0.0, plan.GetSourcePosition(0));
        Assert.Equal(
            380.0,
            plan.GetSourcePosition(
                plan.OutputFrameCount - 1));
    }

    [Fact]
    public void ShortestHemisphereSlerpAndSingleFrame()
    {
        TransformTRS first = new(
            Vector3D.Zero,
            QuaternionD.Identity,
            Vector3D.One);
        double halfAngle = Math.PI / 4.0;
        TransformTRS second = new(
            new Vector3D(10.0, 0.0, 0.0),
            new QuaternionD(
                0.0,
                -Math.Sin(halfAngle),
                0.0,
                -Math.Cos(halfAngle)),
            new Vector3D(1.0, 2.0, 1.0));
        ImmutableArray<ImmutableArray<TransformTRS>> source =
        [
            [first],
            [second],
        ];

        Anm2TemporalResampleResult result =
            Anm2TemporalResampler.Resample(
                source,
                inputFramesPerSecond: 1.0,
                outputFramesPerSecond: 2.0);

        Assert.Equal(3, result.FrameCount);
        QuaternionD expectedMidpoint =
            QuaternionD.FromAxisAngle(
                Vector3D.UnitY,
                Math.PI / 4.0);
        Assert.InRange(
            Math.Abs(QuaternionD.Dot(
                result.Frames[1][0].Rotation,
                expectedMidpoint)),
            1.0 - 1.0e-12,
            1.0 + 1.0e-12);
        Assert.True(
            QuaternionD.Dot(
                result.Frames[1][0].Rotation,
                result.Frames[2][0].Rotation) >=
            0.0);

        ImmutableArray<ImmutableArray<TransformTRS>> one =
            [source[0]];
        Anm2TemporalResampleResult oneResult =
            Anm2TemporalResampler.Resample(
                one,
                inputFramesPerSecond: 30.0,
                outputFramesPerSecond: 24.0);
        Assert.Equal(1, oneResult.FrameCount);
        Assert.Equal(
            one[0][0],
            oneResult.Frames[0][0]);
    }

    [Fact]
    public void SourcePositionUsesOverflowSafeCadenceRatio()
    {
        Anm2TemporalResamplePlan plan =
            Anm2TemporalResampler.CreatePlan(
                sourceFrameCount: 4,
                inputFramesPerSecond: double.MaxValue,
                outputFramesPerSecond: double.MaxValue);

        Assert.Equal(4, plan.OutputFrameCount);
        Assert.Equal(0.0, plan.GetSourcePosition(0));
        Assert.Equal(1.0, plan.GetSourcePosition(1));
        Assert.Equal(2.0, plan.GetSourcePosition(2));
        Assert.Equal(3.0, plan.GetSourcePosition(3));
    }

    [Fact]
    public void DefaultBoundRejectsMillionFrameMaterialization()
    {
        ImmutableArray<ImmutableArray<TransformTRS>> source =
            CreateFrames(2);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => Anm2TemporalResampler.Resample(
                source,
                inputFramesPerSecond: 1.0,
                outputFramesPerSecond: 1_000_000.0));

        Assert.Contains(
            "1,000,001 transforms",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "1,000,000",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResamplingHonorsCancellation()
    {
        ImmutableArray<ImmutableArray<TransformTRS>> source =
            CreateFrames(2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => Anm2TemporalResampler.Resample(
                source,
                inputFramesPerSecond: 30.0,
                outputFramesPerSecond: 24.0,
                maximumOutputTransformCount:
                    Anm2TemporalResampler
                        .MaximumOutputTransformCount,
                cancellationToken: cancellation.Token));
    }

    private static ImmutableArray<
        ImmutableArray<TransformTRS>> CreateFrames(
        int frameCount)
    {
        var frames =
            ImmutableArray.CreateBuilder<
                ImmutableArray<TransformTRS>>(
                frameCount);
        for (int frame = 0;
             frame < frameCount;
             frame++)
        {
            double amount = frameCount == 1
                ? 0.0
                : (double)frame / (frameCount - 1);
            frames.Add(
            [
                new TransformTRS(
                    new Vector3D(
                        10.0 * amount,
                        0.0,
                        0.0),
                    QuaternionD.FromAxisAngle(
                        Vector3D.UnitY,
                        Math.PI * amount),
                    new Vector3D(
                        1.0,
                        1.0 + amount,
                        1.0)),
            ]);
        }

        return frames.MoveToImmutable();
    }
}

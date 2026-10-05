using System.Globalization;
using System.Text.Json;
using ReAnimated.Cli;

namespace ReAnimated.Tests;

[Collection("Character CLI")]
public sealed class CharacterShapeInputsCommandTests
{
    [Fact]
    public async Task CapsuleInputsAreReportedWithoutOpeningAPackage()
    {
        var result = await Run(["character", "shape-inputs", "capsule", "--spans", "1,2,4", "--scale", "2"]);
        Assert.True(result.Code == 0, result.Output);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("dl-reanimated-ragdoll-shape-inputs-v1", json.RootElement.GetProperty("format").GetString());
        Assert.Equal("Z", json.RootElement.GetProperty("capsuleAxis").GetString());
        Assert.Equal(2.005, json.RootElement.GetProperty("radius").GetDouble(), 5);
        Assert.Equal(3.0825, json.RootElement.GetProperty("cylinderLength").GetDouble(), 5);
    }

    [Theory]
    [InlineData("unknown", "1,2,3", "1")]
    [InlineData("sphere", "-1,2,3", "1")]
    [InlineData("box", "1,2,3", "NaN")]
    [InlineData("capsule", "1,2", "1")]
    [InlineData("capsule", "1,2,3", "-1")]
    public async Task InvalidShapeInputsReturnAnArgumentFailure(string shape, string spans, string scale)
    {
        Assert.Equal(2, (await Run(["character", "shape-inputs", shape, "--spans", spans, "--scale", scale])).Code);
    }

    [Fact]
    public async Task DefaultScaleAndArgumentValidationUseTheSharedCommandContract()
    {
        var result = await Run(["character", "shape-inputs", "box", "--spans", "1,2,3"]);
        Assert.Equal(0, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(1, json.RootElement.GetProperty("scale").GetDouble());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("radius").ValueKind);
        Assert.Equal(2, (await Run(["character", "shape-inputs", "box", "--spans", "1,2,3", "--spans", "1,2,3"])).Code);
        Assert.Equal(2, (await Run(["character", "shape-inputs", "box", "--unexpected", "1"])).Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(130, (await Run(["character", "shape-inputs", "box", "--spans", "1,2,3"], cancelled.Token)).Code);
    }

    private static async Task<(int Code, string Output)> Run(string[] args, CancellationToken token = default)
    {
        TextWriter beforeOut = Console.Out, beforeError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output); Console.SetError(error);
            int code = await CliApplication.RunAsync(args, token);
            return (code, output.ToString() + error.ToString());
        }
        finally { Console.SetOut(beforeOut); Console.SetError(beforeError); }
    }
}

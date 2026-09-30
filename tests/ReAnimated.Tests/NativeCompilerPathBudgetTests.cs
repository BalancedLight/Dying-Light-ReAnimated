using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class NativeCompilerPathBudgetTests
{
    [Fact]
    public void NativeStagingBudgetAcceptsBoundaryAndRejectsDeepPathsWithAnActionableReason()
    {
        string boundary = new('x', Dl1OfficialModelCompiler.MaximumNativeCompilerPathLength);
        Dl1OfficialModelCompiler.ValidateNativeCompilerPaths([boundary]);
        var error = Assert.Throws<InvalidDataException>(() =>
            Dl1OfficialModelCompiler.ValidateNativeCompilerPaths([boundary + "x"]));
        Assert.Contains("WorkingDirectoryRoot", error.Message, StringComparison.Ordinal);
        Assert.Contains("conservative", error.Message, StringComparison.Ordinal);
    }
}

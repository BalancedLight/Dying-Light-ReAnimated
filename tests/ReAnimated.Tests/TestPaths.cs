namespace ReAnimated.Tests;

internal static class TestPaths
{
    private static readonly string Root = Path.Combine(
        Path.GetTempPath(),
        "dl-reanimated-tests");

    public static string Combine(params string[] segments) =>
        Path.Combine([Root, .. segments]);
}

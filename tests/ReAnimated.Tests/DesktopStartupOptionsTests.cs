using ReAnimated.App.Infrastructure;

namespace ReAnimated.Tests;

public sealed class DesktopStartupOptionsTests
{
    [Fact]
    public void NoArgumentsKeepNormalStartupDefaults()
    {
        DesktopStartupOptions options = DesktopStartupOptions.Parse([]);

        Assert.Null(options.ProjectPath);
        Assert.False(options.SoftwareUi);
    }

    [Fact]
    public void SoftwareUiCanBeRequestedWithoutAProject()
    {
        DesktopStartupOptions options = DesktopStartupOptions.Parse(
            ["--software-ui"]);

        Assert.Null(options.ProjectPath);
        Assert.True(options.SoftwareUi);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProjectAndSoftwareUiAcceptEitherOrder(
        bool barePath,
        bool softwareFirst)
    {
        string relativePath = Path.Combine("sample folder", "scene.DLRAPROJ");
        List<string> arguments = [];
        if (softwareFirst)
        {
            arguments.Add("--software-ui");
        }

        if (!barePath)
        {
            arguments.Add("--project");
        }

        arguments.Add(relativePath);
        if (!softwareFirst)
        {
            arguments.Add("--software-ui");
        }

        DesktopStartupOptions options = DesktopStartupOptions.Parse(arguments);

        Assert.Equal(Path.GetFullPath(relativePath), options.ProjectPath);
        Assert.True(options.SoftwareUi);
    }

    [Fact]
    public void ProjectParsingDoesNotRequireAnExistingFile()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "unavailable.dlraproj");

        DesktopStartupOptions options = DesktopStartupOptions.Parse(
            ["--project", path]);

        Assert.Equal(path, options.ProjectPath);
        Assert.False(options.SoftwareUi);
        Assert.False(File.Exists(path));
    }

    public static TheoryData<string[]> InvalidArguments { get; } = new()
    {
        new[] { "--project" },
        new[] { "--project", "--software-ui" },
        new[] { "--project", " " },
        new[] { "--project", "model.fbx" },
        new[] { "--project", "first.dlraproj", "--project", "second.dlraproj" },
        new[] { "first.dlraproj", "second.dlraproj" },
        new[] { "first.dlraproj", "--project", "second.dlraproj" },
        new[] { "--software-ui", "--software-ui" },
        new[] { "--unrecognized" },
        new[] { "-project", "scene.dlraproj" },
        new[] { "" },
        new[] { "--project", "invalid\0.dlraproj" },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void AmbiguousOrInvalidArgumentsAreRejected(string[] arguments)
    {
        Assert.Throws<ArgumentException>(
            () => DesktopStartupOptions.Parse(arguments));
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => DesktopStartupOptions.Parse(null!));
    }
}

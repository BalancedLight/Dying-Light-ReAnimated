using System.Text.Json;
using System.Runtime.InteropServices;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Fed;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Project;
using ReAnimated.Core.Storage;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.Cli;

internal static class Program
{
    public static Task<int> Main(string[] args) =>
        CliProcess.RunAsync(args);
}

public static class CliProcess
{
    private const uint AttachParentProcess = uint.MaxValue;

    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        TryAttachParentConsole();

        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        bool handlerRegistered = false;
        try
        {
            try
            {
                Console.CancelKeyPress += handler;
                handlerRegistered = true;
            }
            catch (IOException)
            {
                // A GUI-subsystem process can be launched without an attached
                // console. Redirected output still works, but Ctrl+C is then
                // unavailable for that invocation.
            }

            return await CliApplication.RunAsync(
                    args,
                    cancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            if (handlerRegistered)
            {
                Console.CancelKeyPress -= handler;
            }
        }
    }

    private static void TryAttachParentConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (GetConsoleWindow() == nint.Zero)
        {
            _ = AttachConsole(AttachParentProcess);
        }

        // A GUI-subsystem process can have redirected standard handles even
        // when its parent has no attachable console. Reopen the handles in
        // either case so process runners receive the same output as the
        // developer CLI.
        Console.SetOut(
            new StreamWriter(
                Console.OpenStandardOutput())
            {
                AutoFlush = true,
            });
        Console.SetError(
            new StreamWriter(
                Console.OpenStandardError())
            {
                AutoFlush = true,
            });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();
}

public static class CliApplication
{
    public const string DispatchContract =
        "dl-reanimated-cli-dispatch-v1";

    public static IReadOnlyList<string> SupportedCommands { get; } =
        Array.AsReadOnly(
        [
            "version",
            "inspect-anm2",
            "inspect-fbx",
            "inspect-rpack",
            "inspect-fed",
            "new-project",
            "validate-project",
            "discover-dl1",
            "fingerprint-dl1",
            "index-dl1",
            "build-animation-rpack",
            "export-project",
        ]);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static bool IsInvocation(
        IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count > 0 &&
            (IsHelp(args[0]) ||
             SupportedCommands.Contains(
                 args[0],
                 StringComparer.OrdinalIgnoreCase));
    }

    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "version" => PrintVersion(),
                "inspect-anm2" => await InspectAnm2Async(
                    args[1..],
                    cancellationToken).ConfigureAwait(false),
                "inspect-fbx" => await InspectFbxAsync(
                    args[1..],
                    cancellationToken).ConfigureAwait(false),
                "inspect-rpack" => await InspectRpackAsync(
                    args[1..],
                    cancellationToken).ConfigureAwait(false),
                "inspect-fed" => InspectFed(args[1..]),
                "new-project" => NewProject(args[1..]),
                "validate-project" => ValidateProject(args[1..]),
                "discover-dl1" => DiscoverDl1(args[1..]),
                "fingerprint-dl1" => await FingerprintDl1Async(
                    args[1..],
                    cancellationToken).ConfigureAwait(false),
                "index-dl1" => await IndexDl1Async(
                    args[1..],
                    cancellationToken).ConfigureAwait(false),
                "build-animation-rpack" => await BuildAnimationRpackAsync(
                    args[1..],
                    cancellationToken).ConfigureAwait(false),
                "export-project" => await ProjectExportCommand.RunAsync(
                    args[1..],
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            UnauthorizedAccessException or
            FormatException or
            InvalidOperationException or
            JsonException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> InspectAnm2Async(
        string[] args,
        CancellationToken cancellationToken)
    {
        var path = RequirePath(args, "inspect-anm2");
        var clip = await Anm2Reader.ReadFileAsync(
            path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var report = new
        {
            format = "dl-reanimated-anm2-inspection-v1",
            path = Path.GetFullPath(path),
            clip.Name,
            clip.Sha256,
            header = clip.Header,
            trackDescriptors = clip.TrackDescriptors
                .Select(static descriptor => $"0x{descriptor:X8}")
                .ToArray(),
            pageFrameSpans = clip.PageFrameSpans,
            clip.Warnings,
        };

        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return 0;
    }

    private static async Task<int> InspectFbxAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var path = RequirePath(args, "inspect-fbx");
        var document = await FbxBinaryReader.ReadFileAsync(
            path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var report = new
        {
            format = "dl-reanimated-fbx-inspection-v1",
            path = Path.GetFullPath(path),
            document.Version,
            topLevelNodes = document.Nodes.Select(static node => new
            {
                node.Name,
                propertyCount = node.Properties.Length,
                childCount = node.Children.Length,
                node.StartOffset,
                node.EndOffset,
            }),
        };

        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return 0;
    }

    private static async Task<int> InspectRpackAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        string path = RequirePath(args, "inspect-rpack");
        Rp6lArchive archive = await Rp6lArchive.OpenAsync(
            path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var report = new
        {
            format = "dl-reanimated-rp6l-inspection-v1",
            path = Path.GetFullPath(path),
            archive.CacheIdentity,
            archive.Header,
            compression = archive.Chunks
                .GroupBy(static chunk => chunk.Compression)
                .ToDictionary(
                    static group => group.Key.ToString(),
                    static group => group.Count()),
            resourceTypes = archive.Resources
                .GroupBy(static resource => resource.ResourceType)
                .OrderBy(static group => group.Key)
                .Select(group => new
                {
                    resourceType = group.Key,
                    count = group.Count(),
                }),
            resources = archive.Resources.Take(500).Select(static resource => new
            {
                resource.Index,
                resource.Name,
                resource.ResourceType,
                resource.ItemCount,
            }),
            resourcesTruncated = archive.Resources.Count > 500,
        };

        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return 0;
    }

    private static int InspectFed(string[] args)
    {
        string path = RequirePath(args, "inspect-fed");
        FedDocument document = FedReader.Read(path);
        var report = new
        {
            format = "dl-reanimated-fed-inspection-v1",
            path = Path.GetFullPath(path),
            document.Name,
            expressionCount = document.Expressions.Count,
            expressions = document.Expressions.Select(static expression => new
            {
                expression.Name,
                weightCount = expression.Weights.Count,
                expression.Weights,
            }),
        };
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return 0;
    }

    private static int ValidateProject(string[] args)
    {
        string path = RequirePath(args, "validate-project");
        DlraProject project = ProjectSerializer.Load(path);
        var report = new
        {
            format = "dl-reanimated-project-validation-v1",
            valid = true,
            path = Path.GetFullPath(path),
            project.SchemaVersion,
            projectFormat = project.Format,
            project.Game,
            project.ProjectId,
            project.Name,
            assetCount = project.Assets.Length,
            animationCount = project.Animations.Length,
        };
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return 0;
    }

    private static int NewProject(string[] args)
    {
        if (args.Length is < 1 or > 2 || IsHelp(args[0]))
        {
            throw new ArgumentException(
                "Usage: DLReAnimated new-project <output.dlraproj> [name]");
        }

        string path = Path.GetFullPath(args[0]);
        if (File.Exists(path))
        {
            throw new IOException(
                "A new C# project will not overwrite an existing file.");
        }

        if (!string.Equals(
                Path.GetExtension(path),
                ".dlraproj",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "New project paths must use the .dlraproj extension.");
        }

        string name = args.Length == 2
            ? args[1]
            : Path.GetFileNameWithoutExtension(path);
        DlraProject project = DlraProject.Create(name);
        ProjectSerializer.SaveAtomic(project, path);
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                format = "dl-reanimated-project-create-result-v1",
                path,
                project.ProjectId,
                project.Name,
                project.Game,
            },
            JsonOptions));
        return 0;
    }

    private static int DiscoverDl1(string[] args)
    {
        IReadOnlyList<Dl1InstallLocation> locations = SteamInstallDiscovery.Discover(
            explicitInstallPaths: args.Length == 0 ? null : args);
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                format = "dl-reanimated-dl1-discovery-v1",
                locations,
            },
            JsonOptions));
        return locations.Any(static location => location.IsValid) ? 0 : 3;
    }

    private static async Task<int> FingerprintDl1Async(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length > 1 || (args.Length == 1 && IsHelp(args[0])))
        {
            throw new ArgumentException(
                "Usage: DLReAnimated fingerprint-dl1 [install-directory]");
        }

        var service = new Dl1InstalledBuildFingerprintService();
        Dl1InstalledBuildFingerprint? fingerprint = args.Length == 0
            ? await service.TryReadDiscoveredAsync(
                    cancellationToken)
                .ConfigureAwait(false)
            : await service.ReadAsync(
                    args[0],
                    cancellationToken)
                .ConfigureAwait(false);
        if (fingerprint is null)
        {
            Console.Error.WriteLine(
                "No complete Dying Light 1 Steam installation was discovered.");
            return 3;
        }

        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                format =
                    "dl-reanimated-dl1-build-fingerprint-v1",
                fingerprint,
                gameProcessLaunched = false,
            },
            JsonOptions));
        return 0;
    }

    private static async Task<int> IndexDl1Async(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length < 1 || IsHelp(args[0]))
        {
            throw new ArgumentException(
                "Usage: DLReAnimated index-dl1 <install-directory> [index.sqlite] [--rpack-root <path>]...");
        }

        string installPath = Path.GetFullPath(args[0]);
        int cursor = 1;
        string databasePath =
            cursor < args.Length &&
            !args[cursor].Equals(
                "--rpack-root",
                StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(args[cursor++])
            : LocalApplicationPaths.CreateDefault().AssetIndexFile;
        List<string> additionalRpackRoots = [];
        while (cursor < args.Length)
        {
            if (!args[cursor].Equals(
                    "--rpack-root",
                    StringComparison.OrdinalIgnoreCase) ||
                cursor + 1 >= args.Length)
            {
                throw new ArgumentException(
                    "Additional pack roots must use '--rpack-root <path>'.");
            }

            additionalRpackRoots.Add(
                Path.GetFullPath(args[cursor + 1]));
            cursor += 2;
        }

        await using Dl1RetailProviderSet providers =
            Dl1RetailProviderSet.Create(
                installPath,
                additionalRpackRoots: additionalRpackRoots);
        await using var index = new RetailAssetSqliteIndex(databasePath);
        RetailAssetCatalog catalog = await RetailAssetCatalog.BuildAsync(
            providers.Providers,
            index,
            cancellationToken).ConfigureAwait(false);
        var report = new
        {
            format = "dl-reanimated-dl1-index-v1",
            installPath,
            databasePath,
            rpackSourceCount = providers.RpackProvider.Sources.Count,
            providerDiagnostics = providers.Diagnostics,
            rpackSourceErrors = providers.RpackProvider.SourceErrors,
            assetCount = catalog.Assets.Count,
            conflictCount = catalog.Conflicts.Count,
            byNamespace = catalog.Assets
                .GroupBy(static asset => asset.Id.Namespace)
                .ToDictionary(
                    static group => group.Key.ToString(),
                    static group => group.Count()),
            byResourceType = catalog.Assets
                .Where(static asset =>
                    asset.Id.Namespace == RetailAssetNamespace.RpackResource)
                .GroupBy(static asset => asset.Id.ResourceType)
                .OrderBy(static group => group.Key)
                .ToDictionary(
                    static group => group.Key.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    static group => group.Count()),
        };
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return 0;
    }

    private static async Task<int> BuildAnimationRpackAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length != 2 || IsHelp(args[0]))
        {
            throw new ArgumentException(
                "Usage: DLReAnimated build-animation-rpack <manifest.json> <output.rpack>");
        }

        string manifestPath = Path.GetFullPath(args[0]);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Build manifest was not found.", manifestPath);
        }

        await using var manifestStream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (manifestStream.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Animation-library manifests are limited to 16 MiB.");
        }

        AnimationLibraryBuildManifest manifest =
            await JsonSerializer.DeserializeAsync<AnimationLibraryBuildManifest>(
                manifestStream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Build manifest is empty.");
        if (!string.Equals(
                manifest.Format,
                AnimationLibraryBuildManifest.FormatIdentifier,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Expected manifest format '{AnimationLibraryBuildManifest.FormatIdentifier}'.");
        }

        string manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException("Manifest has no parent directory.");
        var animations = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (AnimationBuildEntry entry in manifest.Animations)
        {
            string input = ResolveManifestPath(manifestDirectory, entry.Path);
            animations.Add(
                entry.Name,
                await File.ReadAllBytesAsync(input, cancellationToken).ConfigureAwait(false));
        }

        var scripts =
            new Dictionary<string, Rp6lAnimationScript>(StringComparer.OrdinalIgnoreCase);
        foreach (AnimationScriptBuildEntry entry in manifest.AnimationScripts)
        {
            if (entry.Sequences.Length != 0)
            {
                if (!string.IsNullOrWhiteSpace(entry.HeaderPath) ||
                    !string.IsNullOrWhiteSpace(entry.BodyPath))
                {
                    throw new InvalidDataException(
                        $"Script '{entry.Name}' must use either sequences or section paths.");
                }

                AnimationScrSections built = AnimationScrCodec.Build(
                    entry.Sequences.Select(static sequence =>
                        new AnimationScrSequence(
                            sequence.Name,
                            sequence.Anm2Name,
                            sequence.StartFrame,
                            sequence.EndFrame,
                            sequence.FramesPerSecond,
                            sequence.Enabled,
                            sequence.Blend)));
                scripts.Add(
                    entry.Name,
                    new Rp6lAnimationScript(
                        built.RecordsAndNames,
                        built.IndexAndNames));
            }
            else
            {
                string header = ResolveManifestPath(
                    manifestDirectory,
                    entry.HeaderPath
                    ?? throw new InvalidDataException(
                        $"Script '{entry.Name}' has no header section."));
                string body = ResolveManifestPath(
                    manifestDirectory,
                    entry.BodyPath
                    ?? throw new InvalidDataException(
                        $"Script '{entry.Name}' has no body section."));
                scripts.Add(
                    entry.Name,
                    new Rp6lAnimationScript(
                        await File.ReadAllBytesAsync(
                            header,
                            cancellationToken).ConfigureAwait(false),
                        await File.ReadAllBytesAsync(
                            body,
                            cancellationToken).ConfigureAwait(false)));
            }
        }

        string outputPath = Path.GetFullPath(args[1]);
        if (string.IsNullOrWhiteSpace(manifest.AppendFrom))
        {
            await Rp6lAnimationLibraryCodec.WriteAtomicAsync(
                outputPath,
                Rp6lAnimationLibraryCodec.Build(animations, scripts),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string existing = ResolveManifestPath(
                manifestDirectory,
                manifest.AppendFrom);
            await Rp6lAnimationLibraryCodec.AppendAtomicAsync(
                existing,
                outputPath,
                animations,
                scripts,
                manifest.ReplaceExisting
                    ? Rp6lAppendConflictPolicy.Replace
                    : Rp6lAppendConflictPolicy.Fail,
                cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                format = "dl-reanimated-animation-library-build-result-v1",
                outputPath,
                animationCount = animations.Count,
                animationScriptCount = scripts.Count,
                appended = !string.IsNullOrWhiteSpace(manifest.AppendFrom),
            },
            JsonOptions));
        return 0;
    }

    private static string ResolveManifestPath(string baseDirectory, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
        {
            throw new InvalidDataException(
                $"Manifest input '{path}' must be project-relative.");
        }

        string root = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(Path.Combine(root, path));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(resolved))
        {
            throw new FileNotFoundException(
                $"Manifest input '{path}' is missing or escapes its directory.",
                resolved);
        }

        return resolved;
    }

    private static string RequirePath(string[] args, string command)
    {
        if (args.Length != 1 || IsHelp(args[0]))
        {
            throw new ArgumentException($"Usage: DLReAnimated {command} <path>");
        }

        var path = Path.GetFullPath(args[0]);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Input file was not found.", path);
        }

        return path;
    }

    private static int PrintVersion()
    {
        var version = typeof(CliApplication).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        Console.WriteLine(version);
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintHelp();
        return 2;
    }

    private static bool IsHelp(string value) =>
        value is "-h" or "--help" or "help" or "/?";

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            DL ReAnimated C# — Dying Light 1 animation authoring

            Usage:
              DLReAnimated version
              DLReAnimated inspect-anm2 <path>
              DLReAnimated inspect-fbx <path>
              DLReAnimated inspect-rpack <path>
              DLReAnimated inspect-fed <path>
              DLReAnimated new-project <output.dlraproj> [name]
              DLReAnimated validate-project <path>
              DLReAnimated discover-dl1 [explicit-install ...]
              DLReAnimated fingerprint-dl1 [install-directory]
              DLReAnimated index-dl1 <install-directory> [index.sqlite] [--rpack-root <path>]...
              DLReAnimated build-animation-rpack <manifest.json> <output.rpack>
              DLReAnimated export-project <project.dlraproj> <dl1-install> <output-directory> [animation-id-or-name] [body|mimic|both]

            The C# project format is DL1-only. Legacy Python projects are never
            migrated or overwritten by this application.
            """);
    }
}

internal sealed record AnimationLibraryBuildManifest
{
    public const string FormatIdentifier =
        "dl-reanimated-animation-library-build-v1";

    public string Format { get; init; } = string.Empty;

    public AnimationBuildEntry[] Animations { get; init; } = [];

    public AnimationScriptBuildEntry[] AnimationScripts { get; init; } = [];

    public string? AppendFrom { get; init; }

    public bool ReplaceExisting { get; init; }
}

internal sealed record AnimationBuildEntry
{
    public string Name { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;
}

internal sealed record AnimationScriptBuildEntry
{
    public string Name { get; init; } = string.Empty;

    public string? HeaderPath { get; init; }

    public string? BodyPath { get; init; }

    public AnimationScrSequenceBuildEntry[] Sequences { get; init; } = [];
}

internal sealed record AnimationScrSequenceBuildEntry
{
    public string Name { get; init; } = string.Empty;

    public string Anm2Name { get; init; } = string.Empty;

    public float StartFrame { get; init; }

    public float EndFrame { get; init; }

    public float FramesPerSecond { get; init; } = 30;

    public int Enabled { get; init; } = 1;

    public float Blend { get; init; } = 0.5f;
}

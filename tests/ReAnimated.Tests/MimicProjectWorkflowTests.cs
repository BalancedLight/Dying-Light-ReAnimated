using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class MimicProjectWorkflowTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-MimicWorkflow-{Guid.NewGuid():N}");

    [Fact]
    public async Task WpfReopenFailsClosedForUnprovenLegacyAnm2Binding()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string sourceDirectory = Path.Combine(
            _temporaryDirectory,
            "Sources");
        Directory.CreateDirectory(sourceDirectory);
        RigDefinition rig = CreateRig("wpf-reopen-target");
        FrameRate rate = new(30, 1);
        AnimationClip body = CreateBody(rig, rate, 3);
        double[] expected = [0.25, 0.75, 0.5];
        byte[] bodyBytes = Anm2DomainAdapter.ExportBody(
            body,
            rig,
            [rig.Bones[0].DescriptorHash!.Value]);
        byte[] mimicBytes = CreateMimicBytes(
            rig,
            rate,
            expected);
        await File.WriteAllBytesAsync(
            Path.Combine(
                sourceDirectory,
                "body.anm2"),
            bodyBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(
                sourceDirectory,
                "face.anm2"),
            mimicBytes);
        Guid bodyAssetId = Guid.NewGuid();
        Guid mimicAssetId = Guid.NewGuid();
        Guid targetAssetId = Guid.NewGuid();
        ProjectAssetReference targetAsset =
            CreateRetailAsset(targetAssetId);
        var animation = new ProjectAnimation
        {
            Name = "Reopen body",
            SourceAssetId = bodyAssetId,
            MimicAssetId = mimicAssetId,
            TargetAssetId = targetAssetId,
            TargetRigId = rig.Id,
            TargetRigSignature =
                RigSignature.Compute(rig),
            FrameRate = rate,
            FrameCount = body.FrameCount,
        };
        DlraProject project =
            DlraProject.Create("WPF reopen") with
            {
                Assets =
                [
                    new ProjectAssetReference
                    {
                        Id = bodyAssetId,
                        Kind =
                            ProjectAssetKind
                                .SourceAnimation,
                        RelativePath =
                            "Sources/body.anm2",
                        ContentSha256 =
                            Sha256(bodyBytes),
                    },
                    new ProjectAssetReference
                    {
                        Id = mimicAssetId,
                        Kind =
                            ProjectAssetKind
                                .SourceAnimation,
                        RelativePath =
                            "Sources/face.anm2",
                        ContentSha256 =
                            Sha256(mimicBytes),
                    },
                    targetAsset,
                ],
                Animations = [animation],
            };
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "wpf-reopen.dlraproj");
        ProjectSerializer.SaveAtomic(
            project,
            projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "wpf-reopen-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "wpf-reopen-cache"));
        await using var viewModel =
            new MainWindowViewModel(
                new JsonWorkspaceStateStore(
                    Path.Combine(
                        _temporaryDirectory,
                        "wpf-reopen-workspace.json")),
                new MimicProjectFileDialogs(
                    projectPath,
                    Path.Combine(
                        sourceDirectory,
                        "face.anm2")),
                assets);

        await viewModel.OpenWorkspaceCommand
            .ExecuteAsync(null);

        Assert.Null(
            GetPrivateField(
                viewModel,
                "_pendingAnm2SourcePath"));
        Assert.Null(
            GetPrivateField(
                viewModel,
                "_pendingMimicSourcePath"));
        Assert.Null(
            GetPrivateField(
                viewModel,
                "_synchronizedAnimation"));
        Assert.Contains(
            viewModel.Diagnostics,
            entry =>
                entry.Area == "ANM2 source binding" &&
                entry.Detail?.Contains(
                    "Rebind Source",
                    StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task WpfCommandImportsDistinctProjectAssetIntoActiveBody()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "wpf-mimic.dlraproj");
        string externalDirectory = Path.Combine(
            _temporaryDirectory,
            "external");
        Directory.CreateDirectory(externalDirectory);
        RigDefinition rig = CreateRig("wpf-target");
        FrameRate rate = new(30, 1);
        AnimationClip body = CreateBody(rig, rate, 3);
        double[] expected = [0.2, 0.8, 0.4];
        byte[] mimicBytes = CreateMimicBytes(
            rig,
            rate,
            expected);
        string selectedMimicPath = Path.Combine(
            externalDirectory,
            "expression.anm2");
        await File.WriteAllBytesAsync(
            selectedMimicPath,
            mimicBytes);
        Guid bodyAssetId = Guid.NewGuid();
        Guid targetAssetId = Guid.NewGuid();
        ProjectAssetReference targetAsset =
            CreateRetailAsset(targetAssetId);
        var animation = new ProjectAnimation
        {
            Name = "WPF body",
            SourceAssetId = bodyAssetId,
            TargetAssetId = targetAssetId,
            TargetRigId = rig.Id,
            TargetRigSignature =
                RigSignature.Compute(rig),
            FrameRate = rate,
            FrameCount = body.FrameCount,
        };
        DlraProject project =
            DlraProject.Create("WPF mimic") with
            {
                Assets =
                [
                    new ProjectAssetReference
                    {
                        Id = bodyAssetId,
                        Kind =
                            ProjectAssetKind
                                .SourceAnimation,
                        RelativePath =
                            "Sources/body.anm2",
                    },
                    targetAsset,
                ],
                Animations = [animation],
            };
        ProjectSerializer.SaveAtomic(
            project,
            projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "wpf-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "wpf-cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(
                    _temporaryDirectory,
                    "wpf-workspace.json")),
            new MimicProjectFileDialogs(
                projectPath,
                selectedMimicPath),
            assets)
        {
            ProjectPath = projectPath,
        };
        SetPrivateField(
            viewModel,
            "_project",
            project);
        SetPrivateField(
            viewModel,
            "_savedProject",
            project);
        SetPrivateField(
            viewModel,
            "_activeAnimationId",
            animation.Id);
        SetPrivateField(
            viewModel,
            "_targetRig",
            rig);
        SetPrivateField(
            viewModel,
            "_targetProjectAsset",
            targetAsset);
        Type sessionType =
            typeof(MainWindowViewModel).GetNestedType(
                "ImportedAnimationSession",
                BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Imported animation session type was not found.");
        object sourceSession =
            Activator.CreateInstance(
                sessionType,
                rig,
                body,
                Path.Combine(
                    _temporaryDirectory,
                    "body.anm2"),
                "DL1 ANM2")
            ?? throw new InvalidOperationException(
                "Could not create the synthetic source session.");
        SetPrivateField(
            viewModel,
            "_sourceAnimation",
            sourceSession);
        SetPrivateField(
            viewModel,
            "_synchronizedAnimation",
            body);

        SetPrivateField(
            viewModel,
            "_targetRig",
            CreateRig("wrong-wpf-target"));
        await viewModel.ImportMimicAnimationCommand
            .ExecuteAsync(null);
        Assert.Null(
            Assert.Single(
                viewModel.CurrentProject.Animations)
            .MimicAssetId);
        Assert.Contains(
            "exact saved target",
            viewModel.StatusText,
            StringComparison.OrdinalIgnoreCase);
        SetPrivateField(
            viewModel,
            "_targetRig",
            rig);
        Assert.True(
            viewModel.ImportMimicAnimationCommand
                .CanExecute(null));
        await viewModel.ImportMimicAnimationCommand
            .ExecuteAsync(null);

        ProjectAnimation updated =
            Assert.Single(
                viewModel.CurrentProject.Animations);
        Guid mimicAssetId =
            Assert.IsType<Guid>(
                updated.MimicAssetId);
        Assert.NotEqual(
            updated.SourceAssetId,
            mimicAssetId);
        ProjectAssetReference mimicAsset =
            Assert.Single(
                viewModel.CurrentProject.Assets,
                asset => asset.Id == mimicAssetId);
        Assert.Equal(
            ProjectAssetKind.SourceAnimation,
            mimicAsset.Kind);
        Assert.Equal(
            Sha256(mimicBytes),
            mimicAsset.ContentSha256);
        Assert.StartsWith(
            "Sources/",
            mimicAsset.RelativePath,
            StringComparison.Ordinal);
        Assert.True(
            File.Exists(
                Path.Combine(
                    _temporaryDirectory,
                    mimicAsset.RelativePath)));
        AnimationClip synchronized =
            Assert.IsType<AnimationClip>(
                GetPrivateField(
                    viewModel,
                    "_synchronizedAnimation"));
        double sampled = synchronized.SampleScalars(
            rate.SecondsForFrame(1))["smile"];
        Assert.InRange(
            Math.Abs(expected[1] - sampled),
            0,
            0.0001);
    }

    [Fact]
    public async Task DistinctMimicAssetPersistsReopensAndExportsActualValues()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateRig("mimic-project-rig");
        FrameRate rate = new(30000, 1001);
        AnimationClip body = CreateBody(rig, rate, 3);
        double[] expectedValues = [0.15, 0.65, 0.35];
        byte[] mimicBytes = CreateMimicBytes(
            rig,
            rate,
            expectedValues);
        string sourcesDirectory = Path.Combine(
            _temporaryDirectory,
            "Sources");
        Directory.CreateDirectory(sourcesDirectory);
        string bodyPath = Path.Combine(
            sourcesDirectory,
            "body.anm2");
        string mimicPath = Path.Combine(
            sourcesDirectory,
            "face.anm2");
        byte[] bodyBytes = Anm2DomainAdapter.ExportBody(
            body,
            rig,
            [rig.Bones[0].DescriptorHash!.Value]);
        await File.WriteAllBytesAsync(bodyPath, bodyBytes);
        await File.WriteAllBytesAsync(mimicPath, mimicBytes);
        string bodyHash = Sha256(bodyBytes);
        string mimicHash = Sha256(mimicBytes);

        SynchronizedMimicAnimation imported =
            await SynchronizedMimicAnm2Loader.LoadAsync(
                mimicPath,
                mimicHash,
                rig,
                body,
                rate,
                body.FrameCount);
        Assert.Equal(mimicHash, imported.Sha256);
        Assert.Single(imported.Mimic.ScalarTracks);
        Assert.Single(imported.Synchronized.TransformTracks);
        Assert.Single(imported.Synchronized.ScalarTracks);

        Guid bodyAssetId = Guid.NewGuid();
        Guid mimicAssetId = Guid.NewGuid();
        DlraProject project = DlraProject.Create("Mimic project") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = bodyAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/body.anm2",
                    ContentSha256 = bodyHash,
                },
                new ProjectAssetReference
                {
                    Id = mimicAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/face.anm2",
                    ContentSha256 = mimicHash,
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "Body with face",
                    SourceAssetId = bodyAssetId,
                    MimicAssetId = mimicAssetId,
                    TargetRigId = rig.Id,
                    FrameRate = rate,
                    FrameCount = body.FrameCount,
                },
            ],
        };
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "face.dlraproj");
        ProjectSerializer.SaveAtomic(project, projectPath);
        DlraProject reopenedProject =
            ProjectSerializer.Load(projectPath);
        ProjectAnimation reopenedAnimation =
            Assert.Single(reopenedProject.Animations);
        Assert.Equal(
            mimicAssetId,
            reopenedAnimation.MimicAssetId);
        ProjectAssetReference reopenedMimic =
            Assert.Single(
                reopenedProject.Assets,
                asset => asset.Id ==
                    reopenedAnimation.MimicAssetId);
        Assert.NotEqual(
            reopenedAnimation.SourceAssetId,
            reopenedMimic.Id);

        SynchronizedMimicAnimation reopened =
            await SynchronizedMimicAnm2Loader.LoadAsync(
                Path.Combine(
                    _temporaryDirectory,
                    reopenedMimic.RelativePath),
                reopenedMimic.ContentSha256!,
                rig,
                body,
                reopenedAnimation.FrameRate,
                reopenedAnimation.FrameCount);
        var map = new RetargetMap(
            rig.Id,
            rig.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.Manual,
                    1),
            ]);
        var evaluation = new EvaluationRequest(
            rig,
            rig,
            reopened.Synchronized,
            0,
            PreviewProfile.RawAuthoring,
            map,
            purpose: EvaluationPurpose.Export);
        Dl1AnimationExportResult exported =
            new Dl1AnimationExporter(
                new Anm2EvaluationAdapter(
                    new AnimationEvaluator()))
            .Export(
                new Dl1AnimationExportRequest
                {
                    Evaluation = evaluation,
                    Parts = Dl1AnimationExportParts.Mimic,
                });
        AnimationClip exportedMimic =
            Anm2DomainAdapter.ImportMimicExact(
                Anm2Reader.Read(
                    Assert.IsType<byte[]>(
                        exported.MimicAnm2),
                    "reopened-export"),
                rig,
                rate);
        for (var frame = 0; frame < expectedValues.Length; frame++)
        {
            double actual = exportedMimic.SampleScalars(
                rate.SecondsForFrame(frame))["smile"];
            Assert.InRange(
                Math.Abs(
                    expectedValues[frame] - actual),
                0,
                0.0001);
        }
    }

    [Fact]
    public async Task LoaderAcceptsIndependentTimingAndPreservesUnknownDescriptors()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        FrameRate rate = new(30, 1);
        RigDefinition rig = CreateRig("target");
        AnimationClip body = CreateBody(rig, rate, 3);
        byte[] twoFrameBytes = CreateMimicBytes(
            rig,
            rate,
            [0.1, 0.2]);
        string twoFramePath = Path.Combine(
            _temporaryDirectory,
            "two-frames.anm2");
        await File.WriteAllBytesAsync(
            twoFramePath,
            twoFrameBytes);

        SynchronizedMimicAnimation shortFacial =
            await SynchronizedMimicAnm2Loader.LoadAsync(
                twoFramePath,
                Sha256(twoFrameBytes),
                rig,
                body,
                rate,
                body.FrameCount);
        Assert.Equal(2, shortFacial.Mimic.FrameCount);
        Assert.Equal(body.FrameCount, shortFacial.Synchronized.FrameCount);
        Assert.Equal(
            0.0,
            shortFacial.Synchronized.SampleScalars(
                rate.SecondsForFrame(2))["smile"]);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => SynchronizedMimicAnm2Loader.LoadAsync(
                twoFramePath,
                new string('0', 64),
                rig,
                body,
                rate,
                body.FrameCount));

        RigDefinition otherRig = CreateRig(
            "other",
            morphName: "frown");
        byte[] unknownDescriptorBytes =
            CreateMimicBytes(
                otherRig,
                rate,
                [0.1, 0.2, 0.3]);
        string unknownPath = Path.Combine(
            _temporaryDirectory,
            "unknown.anm2");
        await File.WriteAllBytesAsync(
            unknownPath,
            unknownDescriptorBytes);
        SynchronizedMimicAnimation unknown =
            await SynchronizedMimicAnm2Loader.LoadAsync(
                unknownPath,
                Sha256(unknownDescriptorBytes),
                rig,
                body,
                rate,
                body.FrameCount);
        Assert.Empty(unknown.Mimic.ScalarTracks);
        Assert.NotNull(unknown.Partition);
        Assert.Single(unknown.Partition.UnresolvedDescriptors);

        AnimationClip mismatchedRateMimic =
            new(
                "rate mismatch",
                new FrameRate(24, 1),
                3,
                scalarTracks:
                [
                    new ScalarTrack(
                        "smile",
                        [
                            new ScalarKeyframe(0, 0),
                            new ScalarKeyframe(1, 0),
                            new ScalarKeyframe(2, 0),
                        ]),
                ]);
        Assert.Throws<ArgumentException>(
            () => AnimationClipSynchronization.Synchronize(
                body,
                mismatchedRateMimic));
    }

    [Fact]
    public void ProjectRejectsMimicReferenceToRetailAsset()
    {
        Guid sourceId = Guid.NewGuid();
        Guid retailId = Guid.NewGuid();
        DlraProject project = DlraProject.Create("Invalid mimic") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/body.anm2",
                },
                CreateRetailAsset(retailId),
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "Invalid",
                    SourceAssetId = sourceId,
                    MimicAssetId = retailId,
                    TargetRigId = "target",
                },
            ],
        };

        ArgumentException exception =
            Assert.Throws<ArgumentException>(
                project.Validate);
        Assert.Contains(
            "retail facial source without an immutable facial binding",
            exception.Message,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(
                _temporaryDirectory,
                recursive: true);
        }
    }

    private static RigDefinition CreateRig(
        string id,
        string morphName = "smile") =>
        new(
            id,
            id,
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash:
                        Dl1NameHash.Compute("root")),
            ],
            [
                new MorphChannelDefinition(
                    0,
                    morphName,
                    Dl1NameHash.Compute(morphName),
                    $"mimic.{morphName}"),
            ]);

    private static AnimationClip CreateBody(
        RigDefinition rig,
        FrameRate rate,
        int frameCount) =>
        new(
            "body",
            rate,
            frameCount,
            [
                new TransformTrack(
                    0,
                    Enumerable.Range(0, frameCount)
                        .Select(frame =>
                            new TransformKeyframe(
                                frame,
                                new TransformTRS(
                                    new Vector3D(
                                        frame,
                                        0,
                                        0),
                                    QuaternionD.Identity,
                                    Vector3D.One)))),
            ]);

    private static byte[] CreateMimicBytes(
        RigDefinition rig,
        FrameRate rate,
        double[] values)
    {
        MorphChannelDefinition morph =
            Assert.Single(rig.MorphChannels);
        var clip = new AnimationClip(
            "mimic",
            rate,
            values.Length,
            scalarTracks:
            [
                new ScalarTrack(
                    morph.Name,
                    values.Select(
                        (value, frame) =>
                            new ScalarKeyframe(
                                frame,
                                value))),
            ]);
        return Anm2DomainAdapter.ExportMimic(
            clip,
            rig,
            [morph.DescriptorHash!.Value]);
    }

    private static ProjectAssetReference CreateRetailAsset(
        Guid id) =>
        new()
        {
            Id = id,
            Kind = ProjectAssetKind.RetailGameResource,
            RelativePath = "retail/272/0",
            ContentSha256 = new string('A', 64),
            RetailIdentity = new ProjectRetailAssetIdentity
            {
                InstallFingerprint = "install",
                ProviderId = "base",
                ProviderPack = "data/common.rpack",
                ResourceType = 272,
                ResourceIndex = 0,
                ResourceName = "target",
                ContentSha256 = new string('A', 64),
            },
        };

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(
                SHA256.HashData(bytes))
            .ToLowerInvariant();

    private static void SetPrivateField(
        MainWindowViewModel viewModel,
        string name,
        object? value)
    {
        FieldInfo field =
            typeof(MainWindowViewModel).GetField(
                name,
                BindingFlags.Instance |
                BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"View-model field '{name}' was not found.");
        field.SetValue(viewModel, value);
    }

    private static object? GetPrivateField(
        MainWindowViewModel viewModel,
        string name)
    {
        FieldInfo field =
            typeof(MainWindowViewModel).GetField(
                name,
                BindingFlags.Instance |
                BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"View-model field '{name}' was not found.");
        return field.GetValue(viewModel);
    }

    private static async Task InvokePrivateTaskAsync(
        MainWindowViewModel viewModel,
        string methodName)
    {
        MethodInfo method =
            typeof(MainWindowViewModel).GetMethod(
                methodName,
                BindingFlags.Instance |
                BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"View-model method '{methodName}' was not found.");
        object? result = method.Invoke(
            viewModel,
            [CancellationToken.None]);
        await Assert.IsAssignableFrom<Task>(result);
    }

    private sealed class MimicProjectFileDialogs(
        string projectPath,
        string mimicPath) :
        IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(
            string? initialPath) =>
            projectPath;

        public string? ShowOpenMimicAnimationDialog(
            string? initialPath) =>
            mimicPath;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) =>
            projectPath;
    }
}

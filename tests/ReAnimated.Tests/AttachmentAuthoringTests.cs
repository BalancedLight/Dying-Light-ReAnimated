using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class AttachmentAuthoringTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-AttachmentTests-{Guid.NewGuid():N}");

    [Fact]
    public void EditorFiltersRetailMeshesAndPrefersPropHelper()
    {
        AttachmentEditorViewModel editor = new();
        editor.ReplaceCatalogAssets(
        [
            new AssetItemViewModel(
                "mesh",
                "police_baton",
                AssetKind.Mesh,
                "dl1-rpack:base",
                "common/meshes/police_baton",
                CreateRetailRecord("police_baton", 7)),
            new AssetItemViewModel(
                "animation",
                "idle",
                AssetKind.Animation,
                "dl1-rpack:base",
                "common/anims/idle",
                CreateRetailRecord("idle", 8)),
        ]);
        var rig = new RigDefinition(
            "dl1:test",
            "Test",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
                new BoneDefinition(
                    1,
                    "weapon_r",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Prop,
                    semanticRole: "prop.right_hand"),
            ]);

        editor.ReplaceParentBones(rig);
        editor.AssetSearch = "baton";

        Assert.Equal(1, editor.CatalogAssetCount);
        Assert.Equal(
            "police_baton",
            Assert.Single(editor.VisibleCatalogAssets).Name);
        Assert.Equal(
            "weapon_r",
            editor.SelectedParentBone?.Name);
    }

    [Fact]
    public void SchemaOneRoundTripPersistsBoneGuardAndLocalTrs()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "attachment.dlraproj");
        Guid sourceId = Guid.NewGuid();
        Guid propId = Guid.NewGuid();
        AttachmentBinding binding = new(
            Guid.NewGuid(),
            propId,
            "Baton",
            4,
            new TransformTRS(
                new Vector3D(0.1, 0.2, 0.3),
                QuaternionD.FromAxisAngle(
                    Vector3D.UnitY,
                    Math.PI / 4),
                new Vector3D(1.0, 1.1, 1.0)),
            AttachmentScope.AuthoredExportable,
            "weapon_r");
        DlraProject project = DlraProject.Create(
            "Attachment") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/source.fbx",
                },
                new ProjectAssetReference
                {
                    Id = propId,
                    Kind = ProjectAssetKind.RetailGameResource,
                    RelativePath = "retail/272/00000007",
                    ResourceId = "rpack:272:police_baton",
                    ContentSha256 = new string('a', 64),
                    RetailIdentity =
                        CreateProjectRetailIdentity(),
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "interaction",
                    SourceAssetId = sourceId,
                    TargetRigId = "dl1:test",
                    FrameCount = 2,
                    Attachments = [binding],
                },
            ],
        };

        ProjectSerializer.SaveAtomic(project, path);
        DlraProject loaded =
            ProjectSerializer.Load(path);
        AttachmentBinding restored = Assert.Single(
            loaded.Animations[0].Attachments);

        Assert.Equal("weapon_r", restored.ParentBoneName);
        Assert.Equal(
            binding.LocalOffset.Translation,
            restored.LocalOffset.Translation);
        Assert.True(
            TransformMatrix.CreateRotation(
                    binding.LocalOffset.Rotation)
                .NearlyEquals(
                    TransformMatrix.CreateRotation(
                        restored.LocalOffset.Rotation)));
        Assert.Equal(
            binding.LocalOffset.Scale,
            restored.LocalOffset.Scale);
        Assert.DoesNotContain(
            "vertices",
            File.ReadAllText(path),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluatorRejectsChangedParentBoneAtSameIndex()
    {
        RigDefinition rig = CreateRig("unexpected_helper");
        AnimationClip clip = CreateIdentityClip(rig);
        AttachmentBinding binding = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Baton",
            1,
            TransformTRS.Identity,
            AttachmentScope.AuthoredExportable,
            "weapon_r");

        EvaluationFrame frame =
            new AnimationEvaluator().Evaluate(
                new EvaluationRequest(
                    rig,
                    rig,
                    clip,
                    0,
                    PreviewProfile.RawAuthoring,
                    attachments: [binding]));

        Assert.Empty(frame.AuthoredAttachments);
        Assert.Empty(frame.DisplayAttachments);
        Assert.Contains(
            frame.Diagnostics,
            diagnostic =>
                diagnostic.Code ==
                "attachment_parent_bone_mismatch" &&
                diagnostic.Severity ==
                EvaluationDiagnosticSeverity.Error);
    }

    [Fact]
    public void ProjectRejectsMoreThanBoundedAttachmentCount()
    {
        Guid sourceId = Guid.NewGuid();
        Guid propId = Guid.NewGuid();
        AttachmentBinding[] attachments =
            Enumerable.Range(
                    0,
                    AttachmentBinding.MaximumPerAnimation + 1)
                .Select(index =>
                    new AttachmentBinding(
                        Guid.NewGuid(),
                        propId,
                        $"Prop {index}",
                        0,
                        TransformTRS.Identity,
                        AttachmentScope
                            .AuthoredExportable,
                        "Bip01"))
                .ToArray();
        DlraProject project = DlraProject.Create(
            "Bounded attachments") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/source.fbx",
                },
                new ProjectAssetReference
                {
                    Id = propId,
                    Kind = ProjectAssetKind.RetailGameResource,
                    RelativePath = "retail/272/00000007",
                    ResourceId = "rpack:272:police_baton",
                    ContentSha256 = new string('a', 64),
                    RetailIdentity =
                        CreateProjectRetailIdentity(),
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "interaction",
                    SourceAssetId = sourceId,
                    TargetRigId = "dl1:test",
                    FrameCount = 2,
                    Attachments =
                        [.. attachments],
                },
            ],
        };

        ArgumentException exception =
            Assert.Throws<ArgumentException>(
                project.Validate);

        Assert.Contains(
            AttachmentBinding.MaximumPerAnimation
                .ToString(
                    System.Globalization
                        .CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RendererComposesStaticAndBindBakedSkinnedProps()
    {
        Guid staticAssetId = Guid.NewGuid();
        Guid skinnedAssetId = Guid.NewGuid();
        EvaluatedAttachment[] evaluated =
        [
            new(
                Guid.NewGuid(),
                staticAssetId,
                "static",
                TransformMatrix.CreateTranslation(
                    new Vector3D(1, 2, 3)),
                AttachmentScope.AuthoredExportable),
            new(
                Guid.NewGuid(),
                skinnedAssetId,
                "skinned",
                TransformMatrix.CreateTranslation(
                    new Vector3D(-2, 0, 0)),
                AttachmentScope.AuthoredExportable),
        ];
        MeshRenderData staticMesh =
            CreateTriangleMesh(
                "static",
                Matrix4x4.CreateTranslation(
                    0.5f,
                    0,
                    0),
                isSkinned: false);
        MeshRenderData skinnedMesh =
            CreateTriangleMesh(
                "skinned",
                Matrix4x4.Identity,
                isSkinned: true) with
            {
                Tint = new Vector4(0.2f, 0.4f, 0.6f, 1.0f),
                BaseColorTexture = new TextureRenderData(
                    "skinned-prop-base-color",
                    4,
                    4,
                    TextureRenderFormat.Bc1Unorm,
                    8,
                    new byte[8]),
                ProjectionRole = MeshProjectionRole.FppHands,
            };
        SkeletonRenderData bindSkeleton = new(
            [
                new BoneRenderData(
                    "root",
                    -1,
                    Matrix4x4.Identity,
                    Matrix4x4.Identity,
                    false),
            ],
            Matrix4x4.Identity);
        Dictionary<Guid, AttachmentRenderAsset> assets = new()
        {
            [staticAssetId] = new(
                staticAssetId,
                "static",
                [staticMesh],
                null),
            [skinnedAssetId] = new(
                skinnedAssetId,
                "skinned",
                [skinnedMesh],
                bindSkeleton),
        };

        AttachmentSceneComposition scene =
            AttachmentSceneComposer.Compose(
                [],
                evaluated,
                assets);

        Assert.Empty(scene.Diagnostics);
        Assert.Equal(2, scene.Meshes.Length);
        MeshRenderData rigidStatic = scene.Meshes[0];
        Assert.False(rigidStatic.IsSkinned);
        Assert.Equal(1.5f, rigidStatic.LocalToWorld.M41, 5);
        Assert.Equal(2.0f, rigidStatic.LocalToWorld.M42, 5);
        Assert.Equal(3.0f, rigidStatic.LocalToWorld.M43, 5);
        MeshRenderData rigidSkinned = scene.Meshes[1];
        Assert.False(rigidSkinned.IsSkinned);
        Assert.True(rigidSkinned.InverseBindMatrices.IsEmpty);
        Assert.Equal(-2.0f, rigidSkinned.LocalToWorld.M41, 5);
        Assert.Equal(
            skinnedMesh.Vertices.Span[0].Position,
            rigidSkinned.Vertices.Span[0].Position);
        Assert.Equal(skinnedMesh.Tint, rigidSkinned.Tint);
        Assert.Same(
            skinnedMesh.BaseColorTexture,
            rigidSkinned.BaseColorTexture);
        Assert.Equal(
            MeshProjectionRole.FppHands,
            rigidSkinned.ProjectionRole);
    }

    [Fact]
    public void RendererReportsMissingRetailAssetWithoutSubstitution()
    {
        Guid missing = Guid.NewGuid();
        var attachment = new EvaluatedAttachment(
            Guid.NewGuid(),
            missing,
            "missing baton",
            TransformMatrix.Identity,
            AttachmentScope.AuthoredExportable);

        AttachmentSceneComposition scene =
            AttachmentSceneComposer.Compose(
                [],
                [attachment],
                new Dictionary<Guid, AttachmentRenderAsset>());

        Assert.Empty(scene.Meshes);
        AttachmentRenderDiagnostic diagnostic =
            Assert.Single(scene.Diagnostics);
        Assert.Equal(
            "attachment_asset_unresolved",
            diagnostic.Code);
        Assert.Contains(
            missing.ToString(),
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewModelAttachmentEditIsUndoableAndInAutosaveSnapshot()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "attachment-editor.dlraproj");
        Guid sourceId = Guid.NewGuid();
        Guid propId = Guid.NewGuid();
        Guid bindingId = Guid.NewGuid();
        DlraProject project = DlraProject.Create(
            "Attachment editor") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/missing.fbx",
                },
                new ProjectAssetReference
                {
                    Id = propId,
                    Kind = ProjectAssetKind.RetailGameResource,
                    RelativePath = "retail/272/00000007",
                    ResourceId = "rpack:272:police_baton",
                    ContentSha256 = new string('a', 64),
                    RetailIdentity =
                        CreateProjectRetailIdentity(),
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "interaction",
                    SourceAssetId = sourceId,
                    TargetRigId = "dl1:test",
                    FrameCount = 2,
                    Attachments =
                    [
                        new AttachmentBinding(
                            bindingId,
                            propId,
                            "Baton",
                            1,
                            TransformTRS.Identity,
                            AttachmentScope
                                .AuthoredExportable,
                            "weapon_r"),
                    ],
                },
            ],
        };
        ProjectSerializer.SaveAtomic(
            project,
            projectPath);
        var dialogs =
            new AttachmentProjectFileDialogs(
                projectPath);
        await using var workspace = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "cache"));
        await using var viewModel =
            new MainWindowViewModel(
                new JsonWorkspaceStateStore(
                    Path.Combine(
                        _temporaryDirectory,
                        "workspace.json")),
                dialogs,
                workspace);
        await viewModel.OpenWorkspaceCommand
            .ExecuteAsync(null);
        viewModel.AttachmentEditor.ReplaceParentBones(
            CreateRig("weapon_r"));
        viewModel.AttachmentEditor.SelectedAttachment =
            Assert.Single(
                viewModel.AttachmentEditor.Attachments);
        viewModel.AttachmentEditor.PositionX = 0.5;

        viewModel.ApplyAttachmentCommand.Execute(null);

        Assert.Equal(
            0.5,
            Assert.Single(
                    viewModel.CurrentProject
                        .Animations[0].Attachments)
                .LocalOffset.Translation.X);
        Assert.True(viewModel.IsDirty);
        Assert.Equal(
            0.5,
            Assert.Single(
                    viewModel.CreateSnapshot()
                        .Project!.Animations[0]
                        .Attachments)
                .LocalOffset.Translation.X);

        viewModel.UndoCommand.Execute(null);
        Assert.Equal(
            0,
            Assert.Single(
                    viewModel.CurrentProject
                        .Animations[0].Attachments)
                .LocalOffset.Translation.X);

        viewModel.RedoCommand.Execute(null);
        Assert.Equal(
            0.5,
            Assert.Single(
                    viewModel.CurrentProject
                        .Animations[0].Attachments)
                .LocalOffset.Translation.X);
    }

    [Fact]
    public async Task SelectedAttachmentCanBeFramedWithoutActorGeometry()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var workspace = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "frame-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "frame-cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(
                    _temporaryDirectory,
                    "frame-workspace.json")),
            new AttachmentProjectFileDialogs(
                Path.Combine(
                    _temporaryDirectory,
                    "frame.dlraproj")),
            workspace);
        Guid assetId = Guid.NewGuid();
        Guid bindingId = Guid.NewGuid();
        AttachmentBinding binding = new(
            bindingId,
            assetId,
            "Baton",
            1,
            TransformTRS.Identity,
            AttachmentScope.AuthoredExportable,
            "weapon_r");
        ProjectAssetReference asset = new()
        {
            Id = assetId,
            Kind = ProjectAssetKind.RetailGameResource,
            RelativePath = "retail/272/00000007",
            ResourceId = "rpack:272:police_baton",
            ContentSha256 = new string('a', 64),
            RetailIdentity = CreateProjectRetailIdentity(),
        };
        viewModel.AttachmentEditor.ReplaceBindings(
            [binding],
            new Dictionary<Guid, ProjectAssetReference>
            {
                [assetId] = asset,
            },
            CreateRig("weapon_r"));
        viewModel.AttachmentEditor.SelectedAttachment =
            Assert.Single(
                viewModel.AttachmentEditor.Attachments);
        MeshRenderData attachmentMesh =
            CreateTriangleMesh(
                $"attachment/{bindingId:N}/baton",
                Matrix4x4.CreateTranslation(
                    12.0f,
                    2.0f,
                    -3.0f),
                isSkinned: false);
        viewModel.SetTargetPreviewScene(
            [attachmentMesh],
            skeleton: null);

        Assert.True(
            viewModel.FrameAttachmentCommand
                .CanExecute(null));
        viewModel.FrameAttachmentCommand.Execute(null);

        RenderCamera camera =
            viewModel.TargetViewport.SceneSource
                .CaptureFrame()
                .Camera;
        Assert.InRange(camera.Target.X, 12.49f, 12.51f);
        Assert.InRange(camera.Target.Y, 2.49f, 2.51f);
        Assert.InRange(camera.Target.Z, -3.01f, -2.99f);
        Assert.Contains(
            "Framed attachment Baton",
            viewModel.StatusText,
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
        string childName) =>
        new(
            "dl1:test",
            "Test",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
                new BoneDefinition(
                    1,
                    childName,
                    0,
                    TransformTRS.Identity,
                    BoneKind.Helper),
            ]);

    private static AnimationClip CreateIdentityClip(
        RigDefinition rig) =>
        new(
            "identity",
            new FrameRate(30, 1),
            frameCount: 1,
            transformTracks: rig.Bones.Select(bone =>
                new TransformTrack(
                    bone.Index,
                    [
                        new TransformKeyframe(
                            0,
                            bone.LocalBindPose),
                    ])));

    private static MeshRenderData CreateTriangleMesh(
        string id,
        Matrix4x4 localToWorld,
        bool isSkinned)
    {
        Vector4 weights =
            isSkinned ? Vector4.UnitX : Vector4.Zero;
        MeshVertex[] vertices =
        [
            new(
                Vector3.Zero,
                Vector3.UnitZ,
                Vector2.Zero,
                weights,
                Vector4.Zero),
            new(
                Vector3.UnitX,
                Vector3.UnitZ,
                Vector2.UnitX,
                weights,
                Vector4.Zero),
            new(
                Vector3.UnitY,
                Vector3.UnitZ,
                Vector2.UnitY,
                weights,
                Vector4.Zero),
        ];
        return new MeshRenderData(
            id,
            vertices,
            new uint[] { 0, 1, 2 },
            localToWorld,
            isSkinned
                ? new[] { Matrix4x4.Identity }
                : ReadOnlyMemory<Matrix4x4>.Empty,
            isSkinned);
    }

    private static ReAnimated.DL1.Assets.Catalog.RetailAssetRecord
        CreateRetailRecord(
            string name,
            int sourceIndex)
    {
        const string hash =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var logical =
            ReAnimated.DL1.Assets.Catalog
                .RetailAssetLogicalId.Rpack(
                    272,
                    name);
        var id =
            ReAnimated.DL1.Assets.Catalog.RetailAssetId
                .Create(
                    logical,
                    "install",
                    "dl1-rpack",
                    sourceIndex,
                    0,
                    hash);
        var source =
            new ReAnimated.DL1.Assets.Catalog
                .RetailAssetSource(
                    "dl1-rpack",
                    ReAnimated.DL1.Assets.Catalog
                        .RetailAssetSourceKind.Rpack,
                    0,
                    "common.rpack",
                    name,
                    sourceIndex,
                    1,
                    1,
                    DateTime.UnixEpoch);
        return new(
            id,
            name,
            source);
    }

    private static ProjectRetailAssetIdentity
        CreateProjectRetailIdentity() =>
        new()
        {
            InstallFingerprint = "install",
            ProviderId = "dl1-rpack",
            ProviderPack = "DW/common.rpack",
            ResourceType = 272,
            ResourceIndex = 7,
            ResourceName = "police_baton",
            Precedence = 0,
            ContentSha256 =
                new string('a', 64),
        };

    private sealed class AttachmentProjectFileDialogs(
        string projectPath) :
        IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(
            string? initialPath) =>
            projectPath;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) =>
            projectPath;

        public string?
            ShowSelectAdditionalRpackRootDialog(
                string? initialPath) =>
            null;
    }
}

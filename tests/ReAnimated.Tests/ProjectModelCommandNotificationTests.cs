using System.Reflection;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectModelCommandNotificationTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task ProjectModelCommandsReevaluateAfterProjectRefreshAndBusyTransition()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var viewModel = new MainWindowViewModel(
                new JsonWorkspaceStateStore(Path.Combine(root, "state.json")));
            Guid modelId = Guid.NewGuid();
            Guid assetId = Guid.NewGuid();
            DlraProject project = DlraProject.Create("Synthetic") with
            {
                Assets =
                [
                    new ProjectAssetReference
                    {
                        Id = assetId,
                        Kind = ProjectAssetKind.CustomModelSource,
                        RelativePath = "models/synthetic.dlrmodel",
                        ContentSha256 = new string('a', 64),
                    },
                ],
                Models =
                [
                    new ProjectModelEntry
                    {
                        Id = modelId,
                        AssetId = assetId,
                        Name = "Synthetic model",
                        RigSignature = new string('b', 64),
                    },
                ],
                Workflow = new ProjectWorkflowState
                {
                    SelectedModelId = modelId,
                },
            };

            project.Validate();
            typeof(MainWindowViewModel)
                .GetMethod(
                    "SetProject",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, [project, true, true, true]);
            typeof(MainWindowViewModel)
                .GetMethod(
                    "RefreshProjectModelLibrary",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, null);

            Assert.NotNull(viewModel.SelectedProjectModel);
            Assert.True(viewModel.EditSelectedProjectModelCommand.CanExecute(null));
            Assert.True(viewModel.RemoveSelectedProjectModelCommand.CanExecute(null));

            int editNotifications = 0;
            int removeNotifications = 0;
            viewModel.EditSelectedProjectModelCommand.CanExecuteChanged +=
                (_, _) => editNotifications++;
            viewModel.RemoveSelectedProjectModelCommand.CanExecuteChanged +=
                (_, _) => removeNotifications++;

            SetMainBusy(viewModel, true);
            Assert.False(viewModel.EditSelectedProjectModelCommand.CanExecute(null));
            Assert.False(viewModel.RemoveSelectedProjectModelCommand.CanExecute(null));

            SetMainBusy(viewModel, false);
            Assert.True(viewModel.EditSelectedProjectModelCommand.CanExecute(null));
            Assert.True(viewModel.RemoveSelectedProjectModelCommand.CanExecute(null));
            Assert.True(editNotifications >= 2);
            Assert.True(removeNotifications >= 2);

            int notificationsBeforeReplacement =
                editNotifications + removeNotifications;
            FieldInfo projectField = typeof(MainWindowViewModel).GetField(
                "_project",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            DlraProject retailAssetProject = project with
            {
                Assets =
                [
                    project.Assets[0] with
                    {
                        Kind = ProjectAssetKind.RetailGameResource,
                    },
                ],
            };
            projectField.SetValue(viewModel, retailAssetProject);
            InvokePrivate(viewModel, "NotifyProjectChanged");

            Assert.False(viewModel.EditSelectedProjectModelCommand.CanExecute(null));
            Assert.True(viewModel.RemoveSelectedProjectModelCommand.CanExecute(null));
            Assert.True(
                editNotifications + removeNotifications >
                notificationsBeforeReplacement);

            int notificationsBeforeRemoval =
                editNotifications + removeNotifications;
            projectField.SetValue(
                viewModel,
                retailAssetProject with { Models = [] });
            InvokePrivate(viewModel, "NotifyProjectChanged");
            Assert.False(viewModel.EditSelectedProjectModelCommand.CanExecute(null));
            Assert.False(viewModel.RemoveSelectedProjectModelCommand.CanExecute(null));
            Assert.True(
                editNotifications + removeNotifications >
                notificationsBeforeRemoval);

            int notificationsBeforeRestore =
                editNotifications + removeNotifications;
            projectField.SetValue(viewModel, project);
            InvokePrivate(viewModel, "NotifyProjectChanged");
            Assert.True(viewModel.EditSelectedProjectModelCommand.CanExecute(null));
            Assert.True(viewModel.RemoveSelectedProjectModelCommand.CanExecute(null));
            Assert.True(
                editNotifications + removeNotifications >
                notificationsBeforeRestore);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    private static void SetMainBusy(MainWindowViewModel viewModel, bool value)
    {
        PropertyInfo property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.IsBusy),
            BindingFlags.Instance | BindingFlags.Public)!;
        property.SetValue(viewModel, value);
    }

    private static void InvokePrivate(
        MainWindowViewModel viewModel,
        string methodName) =>
        typeof(MainWindowViewModel)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, null);
}

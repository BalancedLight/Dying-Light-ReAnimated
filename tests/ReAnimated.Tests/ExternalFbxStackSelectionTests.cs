using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;

namespace ReAnimated.Tests;

public sealed class ExternalFbxStackSelectionTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "AnimationImport")]
    public void DefaultSelectionRequiresStacksButNotTargetModels()
    {
        IProjectFileDialogService dialogs = new NullDialogs();
        ExternalFbxAnimationStackSelection selection =
            Assert.IsType<ExternalFbxAnimationStackSelection>(
                dialogs.SelectExternalFbxAnimationStacks(
                    "generic.fbx",
                    [
                        new FbxExternalAnimationStackDescriptor
                        {
                            StackObjectId = 7,
                            Name = "First",
                            LayerNames = ["Base"],
                            Roles = AnimationSourceRoles.Body,
                        },
                        new FbxExternalAnimationStackDescriptor
                        {
                            StackObjectId = 8,
                            Name = "Needs bake",
                            LayerNames = ["A", "B"],
                            Diagnostics =
                            [
                                new FbxExternalAnimationDiagnostic(
                                    "layered_stack_requires_bake",
                                    FbxExternalAnimationDiagnosticSeverity.Error,
                                    "Bake or flatten this stack."),
                            ],
                        },
                    ]));

        Assert.Equal(
            new long[] { 7 },
            selection.StackObjectIds.ToArray());
        Assert.Equal(
            FbxFacialSourceValueUnit.Percent,
            selection.FacialSourceValueUnit);
        selection.Validate();
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "AnimationImport")]
    public void StackRowsDistinguishFacialOnlyFromRigPreparationFailure()
    {
        var facial = new ExternalFbxStackSelectionRow(
            new FbxExternalAnimationStackDescriptor
            {
                StackObjectId = 1,
                Name = "Facial",
                Roles = AnimationSourceRoles.Facial,
            },
            isSelected: false);
        var failed = new ExternalFbxStackSelectionRow(
            new FbxExternalAnimationStackDescriptor
            {
                StackObjectId = 2,
                Name = "Broken rig",
                Roles = AnimationSourceRoles.None,
                Diagnostics =
                [
                    new FbxExternalAnimationDiagnostic(
                        "body_import_failed",
                        FbxExternalAnimationDiagnosticSeverity.Error,
                        "The source rig could not be prepared."),
                ],
            },
            isSelected: false);

        Assert.Equal(
            "Facial-only (no skeletal rig)",
            facial.SourceRig);
        Assert.Equal("Rig preparation failed", failed.SourceRig);
    }

    private sealed class NullDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;
    }
}

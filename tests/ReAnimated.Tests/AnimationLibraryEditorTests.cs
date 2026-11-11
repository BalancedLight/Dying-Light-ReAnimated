using System.Collections.Immutable;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class AnimationLibraryEditorTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void RealDialogRendersGeneratedPreviewAndUpdatesItAfterEdits()
    {
        WpfTestDispatcher.Run(() =>
        {
            ProjectAnimationLibrary dependency = Library(Guid.NewGuid(), "generic_dependency");
            ProjectRetailAssetIdentity retail = RetailScript("generic_stock", 'a');
            ProjectAnimationLibrary library = Library(Guid.NewGuid(), "generic_preview") with
            {
                Imports = [ProjectImport(dependency.Id), RetailImport(retail)],
            };
            var dialog = new AnimationLibraryEditorDialog(new AnimationLibraryEditorRequest
            {
                Assignment = Assignment("generic_first.anm2"),
                Libraries = [library, dependency],
                RetailScripts = [new AnimationLibraryRetailScriptOption(retail, "Generic stock")],
                SelectedLibraryId = library.Id,
            }) { ShowActivated = false };
            try
            {
                dialog.Show();
                PumpDialog(dialog);
                TextBox preview = Assert.Single(Descendants<TextBox>(dialog), box =>
                    BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "GeneratedSourcePreview");
                Assert.True(preview.IsReadOnly);
                Assert.Contains("!include(\"generic_dependency.scr\")", preview.Text);
                Assert.Contains("!include(\"generic_stock.scr\")", preview.Text);
                Assert.Contains("SeqTrack( \"generic_motion\", \"generic_first.anm2\"", preview.Text);

                dialog.ViewModel.OutputAnm2Name = "generic_edited.anm2";
                PumpDialog(dialog);

                Assert.Contains("SeqTrack( \"generic_motion\", \"generic_edited.anm2\"", preview.Text);
                Assert.DoesNotContain("generic_first.anm2", preview.Text);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void CancelOrWindowCloseDiscardsEditsAndReopensOriginalAssignment(bool cancelButton)
    {
        WpfTestDispatcher.Run(() => WithSavedProject((project, path, originalBytes) =>
        {
            Guid variantId = Assert.Single(project.AnimationVariants).Id;
            var dialog = new AnimationLibraryEditorDialog(AnimationLibraryEditorRequest.FromProject(project, variantId));
            Assert.False(ShowModal(dialog, () =>
            {
                dialog.ViewModel.SelectedLibrary!.ResourceName = "generic_discarded";
                dialog.ViewModel.OutputAnm2Name = "generic_discarded.anm2";
                if (cancelButton)
                    InvokeButton(dialog, "Cancel");
                else
                    dialog.Close();
            }));

            Assert.Null(dialog.Result);
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.Equal("generic_original_library", Assert.Single(project.AnimationLibraries).ResourceName);
            var reopened = new AnimationLibraryEditorDialog(AnimationLibraryEditorRequest.FromProject(project, variantId));
            ShowModal(reopened, () =>
            {
                Assert.Equal("generic_original_library", reopened.ViewModel.SelectedLibrary!.ResourceName);
                Assert.Equal("generic_original.anm2", reopened.ViewModel.OutputAnm2Name);
                Assert.Contains("generic_original.anm2", Preview(reopened).Text);
            });
        }));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void SaveAppliesAndPersistsTheEditedAssignmentThenReopensIt()
    {
        WpfTestDispatcher.Run(() => WithSavedProject((project, path, _) =>
        {
            ProjectAnimationVariant originalVariant = Assert.Single(project.AnimationVariants);
            Guid originalLibraryId = Assert.Single(project.AnimationLibraries).Id;
            var dialog = new AnimationLibraryEditorDialog(AnimationLibraryEditorRequest.FromProject(project, originalVariant.Id));
            Assert.True(ShowModal(dialog, () =>
            {
                dialog.ViewModel.SelectedLibrary!.ResourceName = "generic_saved_library";
                TextBox output = Assert.Single(Descendants<TextBox>(dialog), box =>
                    BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "OutputAnm2Name");
                output.Text = "generic_saved.anm2";
                InvokeButton(dialog, "Save SCR assignment");
            }));

            AnimationLibraryAssignmentResult result = Assert.IsType<AnimationLibraryAssignmentResult>(dialog.Result);
            DlraProject updated = result.ApplyTo(project, originalVariant.Id);
            ProjectSerializer.SaveAtomic(updated, path);
            DlraProject loaded = ProjectSerializer.Load(path);
            ProjectAnimationVariant savedVariant = Assert.Single(loaded.AnimationVariants);
            Assert.Equal(originalVariant.Id, savedVariant.Id);
            Assert.Equal(originalLibraryId, savedVariant.OwningAnimationLibraryId);
            Assert.Equal("generic_saved.anm2", savedVariant.OutputAnm2Name);
            Assert.Equal("generic_saved_library", Assert.Single(loaded.AnimationLibraries).ResourceName);
            Assert.Equal(project.AnimationSources, updated.AnimationSources);
            Assert.Equal(project.Models, updated.Models);
            var reopened = new AnimationLibraryEditorDialog(AnimationLibraryEditorRequest.FromProject(loaded, originalVariant.Id));
            ShowModal(reopened, () =>
            {
                Assert.Equal("generic_saved_library", reopened.ViewModel.SelectedLibrary!.ResourceName);
                Assert.Equal("generic_saved.anm2", reopened.ViewModel.OutputAnm2Name);
                Assert.Contains("generic_saved_library.scr", Preview(reopened).Text);
                Assert.Contains("generic_saved.anm2", Preview(reopened).Text);
            });
        }));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void InvalidAssignmentKeepsDialogOpenAndProjectUnchanged()
    {
        WpfTestDispatcher.Run(() => WithSavedProject((project, path, originalBytes) =>
        {
            Guid variantId = Assert.Single(project.AnimationVariants).Id;
            var dialog = new AnimationLibraryEditorDialog(AnimationLibraryEditorRequest.FromProject(project, variantId));
            Assert.False(ShowModal(dialog, () =>
            {
                dialog.ViewModel.OutputAnm2Name = "invalid output.anm2";
                InvokeButton(dialog, "Save SCR assignment");
                Assert.True(dialog.IsVisible);
                Assert.Null(dialog.Result);
                Assert.True(dialog.ViewModel.HasValidationMessage);
                Assert.Contains("never silently renamed", dialog.ViewModel.ValidationMessage);
                Assert.Contains("Complete the assignment", Preview(dialog).Text);
                Assert.Equal(originalBytes, File.ReadAllBytes(path));
                InvokeButton(dialog, "Cancel");
            }));
            Assert.Null(dialog.Result);
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.Equal("generic_original.anm2", Assert.Single(project.AnimationVariants).OutputAnm2Name);
        }));
    }

    private static void WithSavedProject(Action<DlraProject, string, byte[]> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ReAnimated-ScrDialog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            ProjectAnimationLibrary library = Library(Guid.NewGuid(), "generic_original_library");
            DlraProject project = CreateProject();
            project = project with
            {
                AnimationLibraries = [library],
                AnimationVariants = [Assert.Single(project.AnimationVariants) with
                {
                    OwningAnimationLibraryId = library.Id,
                    OutputAnm2Name = "generic_original.anm2",
                }],
            };
            string path = Path.Combine(directory, "assignment.dlraproj");
            ProjectSerializer.SaveAtomic(project, path);
            action(project, path, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TextBox Preview(Window dialog) => Assert.Single(Descendants<TextBox>(dialog), box =>
        BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "GeneratedSourcePreview");

    private static void InvokeButton(Window dialog, string content)
    {
        Button button = Assert.Single(Descendants<Button>(dialog), button => Equals(button.Content, content));
        var provider = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
        provider.Invoke();
        PumpDialog(dialog);
    }

    private static bool? ShowModal(Window dialog, Action action)
    {
        var failures = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
        {
            failures.Add(args.Exception);
            args.Handled = true;
            dialog.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(dialog.Close));
        };
        dialog.Dispatcher.UnhandledException += handler;
        dialog.ShowActivated = false;
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            try
            {
                dialog.UpdateLayout();
                action();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                dialog.Close();
            }
        }));
        bool? result;
        try
        {
            result = dialog.ShowDialog();
        }
        finally
        {
            dialog.Dispatcher.UnhandledException -= handler;
            dialog.Close();
        }
        if (failures.Count != 0)
            throw new AggregateException("The real modal dialog failed.", failures);
        return result;
    }

    // A synchronous constructor-only test misses deferred WPF binding activation.
    // Preserve every dispatcher failure as a test failure; never suppress it.
    private static void PumpDialog(Window dialog)
    {
        var failures = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
        {
            failures.Add(args.Exception);
            args.Handled = true;
        };
        Dispatcher dispatcher = dialog.Dispatcher;
        dispatcher.UnhandledException += handler;
        try
        {
            dialog.UpdateLayout();
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            dispatcher.UnhandledException -= handler;
        }

        if (failures.Count != 0)
            throw new AggregateException("The real dialog failed while activating deferred WPF work.", failures);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (T descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    [Fact]
    public void RenamingALibraryPreservesAuthoredEventsAndCompiledBacking()
    {
        var sequence = new AnimationSequenceUse
        {
            Name = "generic_motion",
            Anm2Name = "generic_motion.anm2",
            SourceEndFrame = 20,
            Events = [new AnimationEvent { LocalFrame = 2.5, EventId = 1012, RequiredSlot = -1 }],
        };
        var library = Library(Guid.NewGuid(), "generic_events") with
        {
            AuthoredScriptText = "// retained source",
            SequenceUses = [sequence],
            ImportedBinaryScript = new AnimationScriptBinaryBacking { RecordsAndNames = [1, 2, 3], IndexAndNames = [4, 5] },
        };
        var viewModel = new AnimationLibraryEditorViewModel(new AnimationLibraryEditorRequest
        {
            Assignment = Assignment("generic_motion.anm2"),
            Libraries = [library],
            SelectedLibraryId = library.Id,
        });
        viewModel.SelectedLibrary!.ResourceName = "generic_renamed_events";

        Assert.True(viewModel.TryCreateResult(out var result));
        var edited = Assert.Single(result!.Libraries);
        Assert.Equal("generic_renamed_events", edited.ResourceName);
        Assert.Equal(library.AuthoredScriptText, edited.AuthoredScriptText);
        Assert.Equal(sequence, Assert.Single(edited.SequenceUses));
        Assert.Equal(library.ImportedBinaryScript, edited.ImportedBinaryScript);
    }

    [Fact]
    public void EmptyProjectCanCreateAndAssignItsFirstLibrary()
    {
        var viewModel = new AnimationLibraryEditorViewModel(
            new AnimationLibraryEditorRequest
            {
                Assignment = Assignment("generic_first_target.anm2"),
            });

        Assert.Null(viewModel.SelectedLibrary);
        viewModel.AddLibrary();

        Assert.True(viewModel.TryCreateResult(out
            AnimationLibraryAssignmentResult? result));
        ProjectAnimationLibrary created = Assert.Single(result!.Libraries);
        Assert.Equal(created.Id, result.OwningLibraryId);
        Assert.Equal(
            "generic_first_target.anm2",
            result.OutputAnm2Name);
        Assert.Equal(
            "animation_library",
            created.ResourceName);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void EditorPreservesOrderedImportsAndBuildsReadableSourcePreview()
    {
        Guid dependencyId = Guid.NewGuid();
        ProjectAnimationLibrary dependency = Library(
            dependencyId,
            "generic_shared_motion");
        ProjectRetailAssetIdentity retail = RetailScript(
            "generic_stock_motion",
            'a');
        ProjectAnimationLibrary owner = Library(
            Guid.NewGuid(),
            "generic_actor_motion") with
        {
            Imports =
            [
                new ProjectAnimationLibraryImport
                {
                    Kind = ProjectAnimationLibraryImportKind.ProjectLibrary,
                    ProjectLibraryId = dependencyId,
                },
                new ProjectAnimationLibraryImport
                {
                    Kind = ProjectAnimationLibraryImportKind.RetailScript,
                    RetailScriptIdentity = retail,
                },
            ],
        };
        var request = new AnimationLibraryEditorRequest
        {
            Assignment = Assignment("generic_actor_target.anm2"),
            Libraries = [owner, dependency],
            RetailScripts =
            [
                new AnimationLibraryRetailScriptOption(
                    retail,
                    "Generic stock motion"),
            ],
            SelectedLibraryId = owner.Id,
        };
        var viewModel = new AnimationLibraryEditorViewModel(request);

        Assert.Equal(owner.Id, viewModel.SelectedLibrary?.Id);
        Assert.Equal(
            ["generic_shared_motion", "generic_stock_motion"],
            viewModel.SelectedLibrary!.Imports
                .Select(static row => row.ResourceName)
                .ToArray());
        Assert.Contains(
            "!include(\"generic_shared_motion.scr\")",
            viewModel.GeneratedSourcePreview,
            StringComparison.Ordinal);
        Assert.Contains(
            "!include(\"generic_stock_motion.scr\")",
            viewModel.GeneratedSourcePreview,
            StringComparison.Ordinal);
        Assert.True(
            viewModel.GeneratedSourcePreview.IndexOf(
                "generic_shared_motion.scr",
                StringComparison.Ordinal) <
            viewModel.GeneratedSourcePreview.IndexOf(
                "generic_stock_motion.scr",
                StringComparison.Ordinal));
        Assert.Contains(
            "SeqTrack( \"generic_motion\", \"generic_actor_target.anm2\"",
            viewModel.GeneratedSourcePreview,
            StringComparison.Ordinal);

        viewModel.SelectedImport = viewModel.SelectedLibrary.Imports[1];
        viewModel.MoveSelectedImport(-1);

        Assert.Equal(
            ["generic_stock_motion", "generic_shared_motion"],
            viewModel.SelectedLibrary.Imports
                .Select(static row => row.ResourceName)
                .ToArray());
    }

    [Fact]
    public void ValidationRejectsMissingImportsAndIndirectCycles()
    {
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        ProjectAnimationLibrary missing = Library(
            firstId,
            "generic_missing_owner") with
        {
            Imports =
            [
                ProjectImport(secondId),
            ],
        };

        InvalidOperationException missingError = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [missing]));
        Assert.Contains(
            "missing project library",
            missingError.Message,
            StringComparison.OrdinalIgnoreCase);

        ProjectAnimationLibrary first = missing;
        ProjectAnimationLibrary second = Library(
            secondId,
            "generic_cycle_dependency") with
        {
            Imports =
            [
                ProjectImport(firstId),
            ],
        };
        InvalidOperationException cycleError = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [first, second]));
        Assert.Contains(
            "cycle",
            cycleError.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationRejectsDuplicateResourceAndRetailIdentities()
    {
        ProjectAnimationLibrary first = Library(
            Guid.NewGuid(),
            "generic_duplicate");
        ProjectAnimationLibrary second = Library(
            Guid.NewGuid(),
            "GENERIC_DUPLICATE");

        InvalidOperationException resourceError = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [first, second]));
        Assert.Contains(
            "type-322 resource name",
            resourceError.Message,
            StringComparison.OrdinalIgnoreCase);

        ProjectRetailAssetIdentity retail = RetailScript(
            "generic_retail_motion",
            'b');
        ProjectAnimationLibrary duplicateImport = Library(
            Guid.NewGuid(),
            "generic_import_owner") with
        {
            Imports =
            [
                RetailImport(retail),
                RetailImport(retail),
            ],
        };
        InvalidOperationException importError = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [duplicateImport]));
        Assert.Contains(
            "same fingerprinted retail script",
            importError.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingExtensionRequiresExactMatchingRetailIdentity()
    {
        ProjectRetailAssetIdentity baseScript = RetailScript(
            "generic_existing_motion",
            'c');
        ProjectAnimationLibrary extension = Library(
            Guid.NewGuid(),
            "different_resource_name") with
        {
            Mode = ProjectAnimationLibraryMode.ExistingScriptExtension,
            ExistingScriptIdentity = baseScript,
        };

        InvalidOperationException error = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [extension]));

        Assert.Contains(
            "keep the resource name",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FingerprintedType322ProjectAssetBecomesExtensionCandidate()
    {
        ProjectRetailAssetIdentity identity = RetailScript(
            "anims_man_all",
            'd');
        DlraProject project = CreateProject();
        project = project with
        {
            Assets = project.Assets.Add(new ProjectAssetReference
            {
                Kind = ProjectAssetKind.RetailGameResource,
                RelativePath = "retail/322/00000007",
                ResourceId = "rpack:322:anims_man_all",
                ContentSha256 = identity.ContentSha256,
                RetailIdentity = identity,
            }),
        };

        AnimationLibraryEditorRequest request =
            AnimationLibraryEditorRequest.FromProject(
                project,
                Assert.Single(project.AnimationVariants).Id);

        AnimationLibraryRetailScriptOption option =
            Assert.Single(request.RetailScripts);
        Assert.Equal("anims_man_all", option.Identity.ResourceName);
        Assert.Equal(322, option.Identity.ResourceType);
    }

    [Fact]
    public void DlcConventionAcceptsNumericSuffixAndWarnsBelowSixty()
    {
        ProjectAnimationLibrary library = Library(
            Guid.NewGuid(),
            "anims_man_all_dlc12");
        var viewModel = new AnimationLibraryEditorViewModel(
            new AnimationLibraryEditorRequest
            {
                Assignment = Assignment("generic_output.anm2"),
                Libraries = [library],
                SelectedLibraryId = library.Id,
            });

        Assert.True(viewModel.TryCreateResult(out _));
        Assert.Contains(
            "below the conventional 60+ range",
            viewModel.SelectedLibrary!.DlcConventionMessage,
            StringComparison.OrdinalIgnoreCase);

        viewModel.SelectedLibrary.ResourceName = "anims_man_all_dlc60";
        Assert.True(viewModel.TryCreateResult(out _));
        Assert.Contains(
            "dlc60",
            viewModel.SelectedLibrary.DlcConventionMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DlcConventionRejectsMalformedSuffix()
    {
        ProjectAnimationLibrary library = Library(
            Guid.NewGuid(),
            "anims_man_all_dlc_preview");

        InvalidOperationException error = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [library]));

        Assert.Contains(
            "must end with '_dlc' followed by decimal digits",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssignmentRejectsNamesTheOutputBuilderWouldSilentlyRewrite()
    {
        ProjectAnimationLibrary library = Library(
            Guid.NewGuid(),
            "generic_exact_library");

        InvalidOperationException outputError = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateAssignment(
                    [library],
                    library.Id,
                    "generic output.anm2"));
        Assert.Contains(
            "never silently renamed",
            outputError.Message,
            StringComparison.OrdinalIgnoreCase);

        InvalidOperationException scriptError = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [library with
                    {
                        ResourceName = "generic script",
                    }]));
        Assert.Contains(
            "exact DL1 resource identity",
            scriptError.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void ResultAssignsOneOwningLibraryAndStableOutputWithoutChangingIds()
    {
        DlraProject project = CreateProject();
        ProjectAnimationVariant original = Assert.Single(
            project.AnimationVariants);
        ProjectAnimationLibrary library = Library(
            Guid.NewGuid(),
            "generic_target_library");
        var result = new AnimationLibraryAssignmentResult(
            [library],
            library.Id,
            "generic_motion_target.anm2");

        DlraProject updated = result.ApplyTo(project, original.Id);

        ProjectAnimationVariant assigned = Assert.Single(
            updated.AnimationVariants);
        Assert.Equal(original.Id, assigned.Id);
        Assert.Equal(library.Id, assigned.OwningAnimationLibraryId);
        Assert.Equal(
            "generic_motion_target.anm2",
            assigned.OutputAnm2Name);
        Assert.Equal(
            project.AnimationSources,
            updated.AnimationSources);
        Assert.Equal(project.Models, updated.Models);
    }

    [Fact]
    public void EditorMakesReplaceBehaviorExplicitAndDoesNotInferIt()
    {
        ProjectAnimationLibrary library = Library(
            Guid.NewGuid(),
            "generic_replace_owner");
        var viewModel = new AnimationLibraryEditorViewModel(
            new AnimationLibraryEditorRequest
            {
                Assignment = Assignment("generic_output.anm2"),
                Libraries = [library],
                SelectedLibraryId = library.Id,
            });

        Assert.Equal(
            ProjectAnimationSequenceCollisionPolicy.Reject,
            viewModel.SelectedLibrary!.SelectedCollision.Value);
        Assert.Contains(
            "reject collisions",
            viewModel.GeneratedSourcePreview,
            StringComparison.OrdinalIgnoreCase);

        viewModel.SelectedLibrary.SelectedCollision = viewModel
            .CollisionOptions.Single(option => option.Value ==
                ProjectAnimationSequenceCollisionPolicy.ReplaceExisting);

        Assert.Contains(
            "Replace existing sequence explicitly",
            viewModel.GeneratedSourcePreview,
            StringComparison.Ordinal);
    }

    private static AnimationLibrarySequenceAssignment Assignment(
        string outputName) =>
        new(
            Guid.NewGuid(),
            "generic_motion",
            outputName,
            31,
            30);

    private static ProjectAnimationLibrary Library(
        Guid id,
        string resourceName) =>
        new()
        {
            Id = id,
            ResourceName = resourceName,
            DisplayName = resourceName.Replace('_', ' '),
        };

    private static ProjectAnimationLibraryImport ProjectImport(Guid id) =>
        new()
        {
            Kind = ProjectAnimationLibraryImportKind.ProjectLibrary,
            ProjectLibraryId = id,
        };

    private static ProjectAnimationLibraryImport RetailImport(
        ProjectRetailAssetIdentity identity) =>
        new()
        {
            Kind = ProjectAnimationLibraryImportKind.RetailScript,
            RetailScriptIdentity = identity,
        };

    private static ProjectRetailAssetIdentity RetailScript(
        string resourceName,
        char fingerprintCharacter) =>
        new()
        {
            InstallFingerprint = new string('1', 64),
            ProviderId = "generic-provider",
            ProviderPack = "data/generic-provider.rpack",
            ResourceType = 322,
            ResourceIndex = 7,
            ResourceName = resourceName,
            Precedence = 3,
            ContentSha256 = new string(fingerprintCharacter, 64),
        };

    private static DlraProject CreateProject()
    {
        Guid sourceAssetId = Guid.NewGuid();
        Guid modelAssetId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        return DlraProject.Create("Generic SCR assignment") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/generic-motion.fbx",
                    ContentSha256 = Sha('1'),
                },
                new ProjectAssetReference
                {
                    Id = modelAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "Models/generic-target.dlrmodel",
                    ContentSha256 = Sha('2'),
                },
            ],
            Models =
            [
                new ProjectModelEntry
                {
                    Id = modelId,
                    AssetId = modelAssetId,
                    Name = "Generic target",
                    RigSignature = Sha('3'),
                },
            ],
            AnimationSources =
            [
                new ProjectAnimationSource
                {
                    Id = sourceId,
                    Name = "Generic motion",
                    SourceAssetId = sourceAssetId,
                    SourceBinding = new ProjectAnimationSourceBinding
                    {
                        Kind = AnimationSourceKind.LocalFbx,
                        AssetId = sourceAssetId,
                        Roles = AnimationSourceRoles.Body,
                        SourceRigSignature = Sha('4'),
                        TimingProvenance =
                            AnimationTimingProvenance.EmbeddedFbx,
                    },
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 31,
                },
            ],
            AnimationVariants =
            [
                new ProjectAnimationVariant
                {
                    Id = variantId,
                    SourceId = sourceId,
                    Name = "Generic target motion",
                    TargetModelId = modelId,
                    TargetRigId = "generic-target",
                    TargetRigSignature = Sha('3'),
                    BindingMode = ProjectAnimationBindingMode.Retarget,
                    BoneMappings =
                    [
                        new ProjectBoneMapping
                        {
                            SourceBoneName = "source_root",
                            TargetBoneName = "target_root",
                            Method = "manual",
                            ComponentPolicy =
                                RetargetComponentPolicy.FullTransform,
                            TransformComponents =
                                RetargetTransformComponents.All,
                        },
                    ],
                },
            ],
        };
    }

    private static string Sha(char value) => new(value, 64);
}

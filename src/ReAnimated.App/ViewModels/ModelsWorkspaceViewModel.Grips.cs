using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed record ModelGripChoice(Guid Id, string Name, string Label);

public sealed partial class ModelsWorkspaceViewModel
{
    private Func<string, CancellationToken, Task<Dl1RigTemplateResolution>>? _resolveGripTemplate;
    private FbxModelAuthoringImportResult? _gripModel;
    private ModelGripChoice? _selectedGrip;
    private string _gripStatus = "Place the grips in the palms, then adjust their position and rotation.";
    private TransformMatrix _selectedGripFrame = TransformMatrix.Identity;
    private TransformTRS _selectedGripTrs = TransformTRS.Identity;

    public ObservableCollection<ModelGripChoice> GripHelpers { get; } = [];
    public ContactVector GripTranslation { get; } = new();
    public ContactVector GripRotation { get; } = new();
    public IAsyncRelayCommand FitHandGripsCommand { get; private set; } = null!;
    public IAsyncRelayCommand RecalibrateHandGripsCommand { get; private set; } = null!;
    public IRelayCommand ApplyGripFrameCommand { get; private set; } = null!;
    public IRelayCommand OpenGripAdjustmentsCommand { get; private set; } = null!;
    public string GripStatus { get => _gripStatus; private set => SetProperty(ref _gripStatus, value); }
    public bool CanFitHandGrips => _model?.Rig is not null && !IsBusy &&
        !UsesStockFppGripFrames &&
        _model.Package.Document.CreateEffectiveBones().Any(b => Dl1GripHelperFitter.IsHandGrip(b.Name));
    public bool CanEditGripFrame => !IsBusy && SelectedGrip is { } selected &&
        !UsesStockFppGripFrames &&
        _model?.Package.Document.RiggingSession?.Recipe.Helpers.Any(h =>
            h.EntityId == selected.Id && h.FramePolicy == RigFramePolicy.Socket) == true;
    private bool UsesStockFppGripFrames => Conformance.IsStockFppRig ||
        _model?.Package.Document.RigConformance?.FitStrategy == CustomModelRigFitStrategy.StockFppRegional;

    public ModelGripChoice? SelectedGrip
    {
        get => _selectedGrip;
        set
        {
            if (!SetProperty(ref _selectedGrip, value)) return;
            LoadGripFrame();
            OnPropertyChanged(nameof(CanEditGripFrame));
            ApplyGripFrameCommand?.NotifyCanExecuteChanged();
            if (value is not null) SelectedBone = Bones.FirstOrDefault(b => b.Name == value.Name);
        }
    }

    private void InitializeGrips(Func<string, CancellationToken, Task<Dl1RigTemplateResolution>>? resolver)
    {
        _resolveGripTemplate = resolver;
        OpenGripAdjustmentsCommand = new RelayCommand(() =>
        {
            IsConformTabSelected = true;
            Conformance.SelectedStudioStage = Conformance.StudioStages.Single(s => s.Stage == RigStudioStage.HelpersAndHooks);
        }, () => HasModel && !IsBusy);
        FitHandGripsCommand = new AsyncRelayCommand(() => FitHandGripsAsync(false), () => CanFitHandGrips);
        RecalibrateHandGripsCommand = new AsyncRelayCommand(() => FitHandGripsAsync(true), () => CanFitHandGrips);
        ApplyGripFrameCommand = new RelayCommand(ApplyGripFrame, () => CanEditGripFrame &&
            GripTranslation.Value.IsFinite && GripRotation.Value.IsFinite);
        GripTranslation.PropertyChanged += (_, _) => ApplyGripFrameCommand.NotifyCanExecuteChanged();
        GripRotation.PropertyChanged += (_, _) => ApplyGripFrameCommand.NotifyCanExecuteChanged();
    }

    private void RefreshGripState()
    {
        if (!ReferenceEquals(_gripModel, _model))
        {
            _gripModel = _model;
            Guid? previous = SelectedGrip?.Id;
            GripHelpers.Clear();
            if (_model?.Package.Document is { RiggingSession: not null } document)
            {
                foreach (RigParentObservation node in RiggingSessions.ObserveSourceHierarchy(document))
                {
                    RigEntityBinding entity = document.RiggingSession.Recipe.Entities.Single(e => e.EntityId == node.EntityId);
                    if (Dl1GripHelperFitter.IsHandGrip(entity.NativeName))
                        GripHelpers.Add(new(entity.EntityId, entity.NativeName,
                            Dl1GripHelperFitter.DisplayName(entity.NativeName)));
                }
            }
            SelectedGrip = GripHelpers.FirstOrDefault(g => g.Id == previous) ?? GripHelpers.FirstOrDefault();
            LoadGripFrame();
        }
        OnPropertyChanged(nameof(CanFitHandGrips));
        OnPropertyChanged(nameof(CanEditGripFrame));
        if (UsesStockFppGripFrames) GripStatus = "Stock FPP uses the reference hand and grip frames.";
        FitHandGripsCommand?.NotifyCanExecuteChanged();
        RecalibrateHandGripsCommand?.NotifyCanExecuteChanged();
        ApplyGripFrameCommand?.NotifyCanExecuteChanged();
        OpenGripAdjustmentsCommand?.NotifyCanExecuteChanged();
    }

    private async Task FitHandGripsAsync(bool recalibrateManual)
    {
        if (_model is not { } source || !CanFitHandGrips) return;
        IsBusy = true;
        NotifyCommands();
        try
        {
            Dl1RigTemplate? template = Conformance.Template;
            string profile = source.Package.Document.RigConformance?.TemplateProfileName ?? Conformance.TemplateProfileName;
            if (template is null || !template.ProfileName.Equals(profile, StringComparison.Ordinal))
                template = _resolveGripTemplate is null ? null :
                    (await _resolveGripTemplate(profile, CancellationToken.None)).Template;
            if (!ReferenceEquals(source, _model)) return;
            if (template is null)
            {
                GripStatus = "Choose a reference skeleton in Setup before fitting the hand grips.";
                return;
            }
            Dl1GripHelperFitResult fit = Dl1GripHelperFitter.Apply(source, template, recalibrateManual);
            if (!ReferenceEquals(fit.Model, source))
                ApplyHelperMutation(fit.Model.Package.Document, SelectedGrip?.Name,
                    "Saved hand-grip position and orientation");
            GripStatus = fit.ChangedHelpers.IsEmpty
                ? "Hand grips are unchanged. Saved manual adjustments are kept."
                : "Hand grips fitted. Position and rotation can be adjusted below.";
            if (!fit.Messages.IsEmpty) GripStatus += " " + string.Join(" ", fit.Messages);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            GripStatus = "Hand grips were not changed: " + error.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private FbxModelAuthoringImportResult FitHandGripsAfterConformance(FbxModelAuthoringImportResult model)
    {
        if (Conformance.Template is not { } template) return model;
        Dl1GripHelperFitResult result = Dl1GripHelperFitter.Apply(model, template);
        GripStatus = result.Messages.IsEmpty
            ? "Hand grips follow the fitted palms. Saved manual adjustments are kept."
            : string.Join(" ", result.Messages);
        return result.Model;
    }

    private void ApplyGripFrame()
    {
        if (!CanEditGripFrame || _model is not { } model || SelectedGrip is not { } selected) return;
        try
        {
            double radians = Math.PI / 180;
            var q = System.Numerics.Quaternion.CreateFromYawPitchRoll(
                (float)(GripRotation.Y * radians), (float)(GripRotation.X * radians), (float)(GripRotation.Z * radians));
            TransformMatrix projected = new TransformTRS(GripTranslation.Value,
                new QuaternionD(q.X, q.Y, q.Z, q.W), _selectedGripTrs.Scale).ToMatrix();
            TransformMatrix local = projected * _selectedGripTrs.ToMatrix().InvertedAffine() * _selectedGripFrame;
            CustomModelDocument updated = RigGripHelperAuthoring.SetLocalFrame(model.Package.Document,
                model.Package.Document.RiggingSession!.CreateJobToken(), selected.Id, local);
            ApplyHelperMutation(updated, selected.Name, "Saved hand-grip adjustment");
            GripStatus = "Grip adjustment saved. Automatic fitting will keep this position and rotation.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            GripStatus = "Grip adjustment was not saved: " + error.Message;
        }
    }

    private void LoadGripFrame()
    {
        if (_model?.Package.Document is not { } document || SelectedGrip is not { } selected) return;
        HelperRecipe? recipe = document.RiggingSession?.Recipe.Helpers.FirstOrDefault(h => h.EntityId == selected.Id);
        if (recipe is null) return;
        _selectedGripFrame = recipe.LocalFrame;
        _selectedGripTrs = ReadGripTransform(recipe.LocalFrame);
        GripTranslation.Set(_selectedGripTrs.Translation);
        QuaternionD q = _selectedGripTrs.Rotation;
        GripRotation.Set(new(
            Math.Asin(Math.Clamp(2 * (q.W * q.X - q.Y * q.Z), -1, 1)) * 180 / Math.PI,
            Math.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y)) * 180 / Math.PI,
            Math.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.X * q.X + q.Z * q.Z)) * 180 / Math.PI));
    }

    private static TransformTRS ReadGripTransform(TransformMatrix frame)
    {
        try { return frame.Decompose(1e-5); }
        catch (InvalidOperationException)
        {
            Vector3D first = new(frame.M11, frame.M21, frame.M31);
            Vector3D second = new(frame.M12, frame.M22, frame.M32);
            Vector3D third = new(frame.M13, frame.M23, frame.M33);
            Vector3D x = first.Normalized();
            Vector3D y = (second - x * Vector3D.Dot(second, x)).Normalized();
            Vector3D z = Vector3D.Cross(x, y).Normalized();
            TransformMatrix rotation = new(x.X, y.X, z.X, 0, x.Y, y.Y, z.Y, 0,
                x.Z, y.Z, z.Z, 0, 0, 0, 0, 1);
            return new(frame.Translation, rotation.Decompose().Rotation,
                new(first.Length, second.Length, Math.CopySign(third.Length, frame.LinearDeterminant)));
        }
    }
}

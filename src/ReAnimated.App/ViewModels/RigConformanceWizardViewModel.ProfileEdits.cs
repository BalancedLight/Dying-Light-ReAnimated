using CommunityToolkit.Mvvm.ComponentModel;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class RigConformanceWizardViewModel
{
    [ObservableProperty] private string _profileEditStatus = string.Empty;
    public bool HasProfileEditRefusal => !string.IsNullOrEmpty(ProfileEditStatus);
    partial void OnProfileEditStatusChanged(string value) => OnPropertyChanged(nameof(HasProfileEditRefusal));

    private bool RequestBodyChange(EventHandler<BodyModelEventArgs>? handler, BodyModelEventArgs change)
    {
        ProfileEditStatus = string.Empty;
        try
        {
            FbxProfileEditGuard.RequireAllowed(change.Source, change.Result);
            handler?.Invoke(this, change);
            return true;
        }
        catch (RigProfileEditException error)
        {
            ProfileEditStatus = "Change not applied. " + error.Message;
            return false;
        }
    }
}

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Models;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    [ObservableProperty] private Dl1DeploymentInspection? _deploymentInspection;
    [ObservableProperty] private string _deploymentInspectionStatus="Check this model's installed files and authoring identity. Active Player resources and gameplay require separate evidence.";
    public IAsyncRelayCommand CheckInstalledDeploymentCommand { get; private set; }=null!;
    public bool CanCheckInstalledDeployment=>HasModel&&!IsBusy&&!string.IsNullOrWhiteSpace(DeveloperToolsProjectRoot);

    private void InitializeDeploymentInspection() => CheckInstalledDeploymentCommand =
        new AsyncRelayCommand(CheckInstalledDeploymentAsync,()=>CanCheckInstalledDeployment);

    private async Task CheckInstalledDeploymentAsync(CancellationToken cancellationToken)
    {
        SyncDocument();if(_model is not { } model)return;
        string root=DeveloperToolsProjectRoot;
        string resource=Dl1SourceModelWriter.SanitizeName(ResourceName,55);
        string character=string.IsNullOrWhiteSpace(CharacterId)?resource:CharacterId.Trim();
        long revision=Volatile.Read(ref _authoringRevision);
        long generation=BeginOperation(cancellationToken,out var token);
        DeploymentInspection=null;DeploymentInspectionStatus="Checking this model's deployment receipt and installed files…";
        try
        {
            var result=await Task.Run(async ()=>
            {
                var receipt=Dl1DeveloperToolsProjectDeployer.LoadLatestActiveReceipt(root,resource,character);
                token.ThrowIfCancellationRequested();
                return receipt is null?null:await Dl1DeveloperToolsProjectDeployer.InspectDeploymentAsync(receipt,root,model,cancellationToken:token).ConfigureAwait(false);
            },token);
            EnsureCurrent(generation,token);
            if(revision!=Volatile.Read(ref _authoringRevision)||root!=DeveloperToolsProjectRoot||_model?.Package.Document.ModelId!=model.Package.Document.ModelId)
            {DeploymentInspectionStatus="The model or project changed during inspection. Check the current selection again.";return;}
            DeploymentInspection=result;
            DeploymentInspectionStatus=result is null?"No valid active deployment receipt matches this model and character in the selected project.":
                $"File snapshot checked {DateTimeOffset.Now:g}. Check again after changes. This result does not establish the active Player provider or gameplay behavior.";
        }
        catch(OperationCanceledException){DeploymentInspectionStatus="Deployment inspection cancelled.";}
        catch(Exception error) when(error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {DeploymentInspectionStatus="Deployment inspection did not complete: "+error.Message;}
        finally{EndOperation(generation);}
    }

    private void InvalidateDeploymentInspection()
    {
        if(DeploymentInspection is null)return;
        DeploymentInspection=null;
        DeploymentInspectionStatus="The model or project changed. Check the installed deployment again before relying on the previous result.";
    }
}

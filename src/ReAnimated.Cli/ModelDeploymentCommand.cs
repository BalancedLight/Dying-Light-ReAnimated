using System.Collections.Immutable;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Cli;

internal static class ModelDeploymentCommand
{
    public static async Task<int> RunAsync(string[] args, JsonSerializerOptions options, CancellationToken token)
    {
        if (args.Length < 7 || args.Skip(7).Any(arg => arg is not ("--stock-bank" or "--preflight")))
            throw new ArgumentException("Usage: DLReAnimated deploy-model <model.dlrmodel> <project-root> <compiler.exe> <retail-Data0.pak> <character-id> <resource-name> <animation-bank> [--stock-bank] [--preflight]");
        var package = CustomModelPackageSerializer.Load(args[0]);
        var model = FbxModelAuthoringImporter.ImportPackage(package, token);
        bool stock = args.Contains("--stock-bank", StringComparer.Ordinal) || package.Document.BuildSettings.ReferenceExistingAnimationLibrary;
        var request = new Dl1DeveloperToolsDeploymentRequest
        {
            Model = model, ProjectRoot = args[1], CompilerExecutablePath = args[2], RetailData0PakPath = args[3],
            CharacterId = args[4], ModelResourceName = args[5], AnimationLibraryName = args[6],
            SurfaceName = package.Document.BuildSettings.SurfaceName,
            ReferenceExistingAnimationLibrary = stock, DeployWithoutAnimations = stock,
            AnimationSelections = stock ? [] : package.Document.AnimationClips,
            InstallLooseAnm2 = !stock, ExportPortableAnimationRpack = !stock,
        };
        if (args.Contains("--preflight", StringComparer.Ordinal))
        {
            var plan = await Dl1DeveloperToolsProjectDeployer.PreflightAsync(request, token);
            Console.WriteLine(JsonSerializer.Serialize(plan, options));
            return plan.CanDeploy ? 0 : 2;
        }
        var result = await Dl1DeveloperToolsProjectDeployer.DeployAsync(request, token);
        Console.WriteLine(JsonSerializer.Serialize(result, options));
        return 0;
    }
}

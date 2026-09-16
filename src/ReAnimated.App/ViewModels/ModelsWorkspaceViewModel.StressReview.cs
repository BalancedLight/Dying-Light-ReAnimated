using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private async Task<StressDeformationReport> MeasureStressAsync(CancellationToken cancellationToken)
    {
        if (_model is not { Rig: not null } source || !Conformance.TryGetStressPreview(out var posed, out var morphs))
            throw new InvalidOperationException("Enable a current stress pose before measuring.");
        CustomModelPreviewMode mode = SelectedPreviewMode.Mode;
        var edits = Conformance.StressOffsets.ToArray();
        double amount = Conformance.StressAmount;
        var report = await Task.Run(() => {
            cancellationToken.ThrowIfCancellationRequested();
            var session = CustomModelPreviewAdapter.CreateSession(source, mode);
            var reference = session.CreateSkeleton(RigStressPose.Evaluate(source.Package.Document, source.Rig!, [], 0));
            var deformed = session.CreateSkeleton(posed!);
            return StressDeformationMeasurement.Measure(session.Meshes, reference, deformed, morphs, cancellationToken);
        }, cancellationToken);
        if (!ReferenceEquals(source, _model) || mode != SelectedPreviewMode.Mode || amount != Conformance.StressAmount ||
            !edits.SequenceEqual(Conformance.StressOffsets)) throw new OperationCanceledException("Stress inputs changed before measurement finished.");
        return report;
    }
}

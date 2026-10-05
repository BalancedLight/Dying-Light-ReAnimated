using ReAnimated.Codecs.CompactMesh;

namespace ReAnimated.Codecs.Models;

public sealed record CharacterBoneBoundsControlReadback(string Bone,string Axis,double Scale,
    CompactBounds BaselineBounds,CompactBounds ControlBounds,int VerifiedNodeCount);

public static class CharacterBoneBoundsControlVerifier
{
    public static CharacterBoneBoundsControlReadback Verify(Dl1PreparedPhysicalNodeReadBackEvidence baseline,
        Dl1PreparedPhysicalNodeReadBackEvidence control,string bone,string axis,double scale)
    {
        ArgumentNullException.ThrowIfNull(baseline);ArgumentNullException.ThrowIfNull(control);
        ArgumentException.ThrowIfNullOrWhiteSpace(bone);
        if(axis is not ("X" or "Y" or "Z") || !double.IsFinite(scale) || scale<=0 || scale==1)
            throw new ArgumentException("Select one axis and a positive scale different from one.");
        if(baseline.Nodes.IsDefaultOrEmpty || control.Nodes.IsDefault || baseline.Nodes.Length!=control.Nodes.Length ||
            baseline.VerifiedNodeCount!=baseline.Nodes.Length || control.VerifiedNodeCount!=control.Nodes.Length)
            throw new InvalidDataException("Both controls need complete physical-node readback.");
        var prior=baseline.Nodes.SingleOrDefault(row=>row.CompiledName==bone)
            ?? throw new InvalidDataException("The measured bone is missing from baseline readback.");
        var changed=control.Nodes.SingleOrDefault(row=>row.CompiledName==bone)
            ?? throw new InvalidDataException("The measured bone is missing from control readback.");
        if(control.Nodes.Select(row=>row.CompiledEntityIndex).Distinct().Count()!=control.Nodes.Length ||
            baseline.Nodes.Select(row=>row.CompiledEntityIndex).Distinct().Count()!=baseline.Nodes.Length)
            throw new InvalidDataException("Control physical-node indices are duplicated.");
        var controlsByIndex=control.Nodes.ToDictionary(row=>row.CompiledEntityIndex);
        foreach(var row in baseline.Nodes)
        {
            if(!controlsByIndex.TryGetValue(row.CompiledEntityIndex,out var other))
                throw new InvalidDataException("The control physical-node inventory changed.");
            if(row.CompiledName!=other.CompiledName || row.CompiledParentIndex!=other.CompiledParentIndex ||
                row.CompiledEntityType!=other.CompiledEntityType || row.CompiledLocalMatrix!=other.CompiledLocalMatrix ||
                row.CompiledReferenceMatrix!=other.CompiledReferenceMatrix)
                throw new InvalidDataException("A bound control changed physical hierarchy or frames.");
            var expected=row.CompiledBounds;
            if(!expected.IsFinite || !other.CompiledBounds.IsFinite || expected.HalfX<0 || expected.HalfY<0 || expected.HalfZ<0 ||
                other.CompiledBounds.HalfX<0 || other.CompiledBounds.HalfY<0 || other.CompiledBounds.HalfZ<0)
                throw new InvalidDataException("The control contains invalid compiled bounds.");
            if(row.CompiledName==bone)expected=axis switch
                {"X"=>expected with {HalfX=(float)(expected.HalfX*scale)},"Y"=>expected with {HalfY=(float)(expected.HalfY*scale)},_=>expected with {HalfZ=(float)(expected.HalfZ*scale)}};
            if(!Equal(expected,other.CompiledBounds))throw new InvalidDataException("The compiled bounds do not match the independent-axis control.");
        }
        if(Equal(prior.CompiledBounds,changed.CompiledBounds))throw new InvalidDataException("The requested dimension did not change.");
        return new(bone,axis,scale,prior.CompiledBounds,changed.CompiledBounds,baseline.Nodes.Length);
    }
    private static bool Equal(CompactBounds a,CompactBounds b)=>
        Math.Abs(a.CenterX-b.CenterX)<=1e-5 && Math.Abs(a.CenterY-b.CenterY)<=1e-5 && Math.Abs(a.CenterZ-b.CenterZ)<=1e-5 &&
        Math.Abs(a.HalfX-b.HalfX)<=1e-5 && Math.Abs(a.HalfY-b.HalfY)<=1e-5 && Math.Abs(a.HalfZ-b.HalfZ)<=1e-5;
}

using System.Collections.Immutable;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class CharacterBoneBoundsControlReadbackTests
{
    [Theory]
    [InlineData("X")]
    [InlineData("Y")]
    [InlineData("Z")]
    public void ExactlyOneCompiledDimensionChangesWhileAllFramesAndOtherNodesStayEqual(string axis)
    {
        var baseline=Evidence();var row=baseline.Nodes[0];var bounds=row.CompiledBounds;
        bounds=axis switch{"X"=>bounds with {HalfX=bounds.HalfX*1.5f},"Y"=>bounds with {HalfY=bounds.HalfY*1.5f},_=>bounds with {HalfZ=bounds.HalfZ*1.5f}};
        var control=baseline with {Nodes=baseline.Nodes.SetItem(0,row with {CompiledBounds=bounds})};
        var receipt=CharacterBoneBoundsControlVerifier.Verify(baseline,control,"bone",axis,1.5);
        Assert.Equal(bounds,receipt.ControlBounds);Assert.Equal(2,receipt.VerifiedNodeCount);
    }
    [Theory]
    [InlineData("other-bound")]
    [InlineData("frame")]
    [InlineData("missing")]
    [InlineData("unchanged")]
    public void ControlCannotHideUnrelatedCompiledChanges(string scenario)
    {
        var baseline=Evidence();var row=baseline.Nodes[0];var control=baseline with {Nodes=baseline.Nodes.SetItem(0,row with {CompiledBounds=row.CompiledBounds with {HalfX=1.5f}})};
        if(scenario=="other-bound")control=control with {Nodes=control.Nodes.SetItem(1,control.Nodes[1] with {CompiledBounds=new(0,0,0,2,2,2)})};
        if(scenario=="frame")control=control with {Nodes=control.Nodes.SetItem(0,control.Nodes[0] with {CompiledReferenceMatrix=CompactMatrix3x4.Identity with {M14=1}})};
        if(scenario=="missing")control=control with {Nodes=[control.Nodes[0]]};
        if(scenario=="unchanged")control=baseline;
        Assert.Throws<InvalidDataException>(()=>CharacterBoneBoundsControlVerifier.Verify(baseline,control,"bone","X",1.5));
    }
    private static Dl1PreparedPhysicalNodeReadBackEvidence Evidence()
    {
        Dl1PreparedPhysicalNodeReadBack Row(int index,string name)
        {
            var expected=new Dl1PreparedPhysicalNodeExpectation(index,name,index-1,8,CompactMeshEntityType.Bone,CompactMatrix3x4.Identity,CompactMatrix3x4.Identity,new(0,0,0,1,2,3));
            return new(expected,name,index,index-1,CompactMeshEntityType.Bone,CompactMatrix3x4.Identity,CompactMatrix3x4.Identity,expected.Bounds,0,0,0);
        }
        return new(new string('a',64),2,[Row(0,"bone"),Row(1,"other")]);
    }
}

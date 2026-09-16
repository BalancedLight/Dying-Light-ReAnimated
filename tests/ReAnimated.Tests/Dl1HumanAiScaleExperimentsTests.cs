using System.Globalization;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1HumanAiScaleExperimentsTests
{
    private static readonly string[] GeneratedNames = ["control","half","normal","double"];
    internal const string Source = """
        // Preset("hidden") { SetField("m_ForcedBodyScaleMin", "99"); }
        sub main()
        {
            PresetDef("Character")
            {
                AddField("m_ForcedBodyScaleMin", "1", false, false);
                AddField("m_ForcedBodyScaleMax", "1", false, false);
                Preset("control")
                {
                    SetField("MeshName", "generic_actor.msh");
                    SetField("SkinName", "default");
                    SetField("Conflict", NEUTRAL);
                    SetField("Macro", Choose("a,b", 2));
                    /* keep the comment and spacing */
                    SetField("m_ForcedBodyScaleMin", "1.1");
                    SetField("m_ForcedBodyScaleMax", "1.2");
                    SetField("Description", "braces { } and // inside strings");
                }
            }
            PresetDef("Other") { Preset("control") { SetField("MeshName", "another.msh"); } }
        }
        """;

    [Fact]
    public void GeneratesFixedSizeTrialsWithoutModifyingOriginalOrOtherFields()
    {
        var result = Dl1HumanAiScaleExperiments.Build(Source,"Character","control",
            [new("half",.5),new("normal",1),new("double",2)]);
        Assert.Equal(GeneratedNames,Dl1HumanAiScaleExperiments.ListPresets(result.Source,"Character"));
        Assert.Equal(Source[..Source.IndexOf("    PresetDef(\"Other\")",StringComparison.Ordinal)].TrimEnd().TrimEnd('}').TrimEnd(),
            result.Source[..result.Source.IndexOf("        Preset(\"half\")",StringComparison.Ordinal)].TrimEnd());
        Assert.Contains("SetField(\"Macro\", Choose(\"a,b\", 2));",result.Source,StringComparison.Ordinal);
        Assert.Equal(4,result.Source.Split("/* keep the comment and spacing */",StringSplitOptions.None).Length-1);
        Assert.Equal(4,result.Source.Split("\"generic_actor.msh\"",StringSplitOptions.None).Length-1);
        Assert.Contains("SetField(\"m_ForcedBodyScaleMin\", \"0.5\");",result.Source,StringComparison.Ordinal);
        Assert.Contains("SetField(\"m_ForcedBodyScaleMax\", \"2\");",result.Source,StringComparison.Ordinal);
        Assert.Equal(3,result.Trials.Length);
        var repeated=Dl1HumanAiScaleExperiments.Build(Source,"Character","control",
            [new("half",.5),new("normal",1),new("double",2)]);
        Assert.Equal(result.Source,repeated.Source);
        Assert.Equal(result.ResultTextSha256,repeated.ResultTextSha256);
        Assert.Equal<Dl1ScaleTrialReceipt>(result.Trials,repeated.Trials);
    }

    [Fact]
    public void MissingAssignmentsAreAppendedButDeclarationsAreRequired()
    {
        string withoutValues=Source.Replace("SetField(\"m_ForcedBodyScaleMin\", \"1.1\");","",StringComparison.Ordinal)
            .Replace("SetField(\"m_ForcedBodyScaleMax\", \"1.2\");","",StringComparison.Ordinal);
        var result=Dl1HumanAiScaleExperiments.Build(withoutValues,"Character","control",[new("size",.5)]);
        Assert.Contains("SetField(\"m_ForcedBodyScaleMin\", \"0.5\");",result.Source,StringComparison.Ordinal);
        Assert.Contains("SetField(\"m_ForcedBodyScaleMax\", \"0.5\");",result.Source,StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(()=>Dl1HumanAiScaleExperiments.Build(
            withoutValues.Replace("AddField(\"m_ForcedBodyScaleMin\"","AddField(\"unrelated\"",StringComparison.Ordinal),"Character","control",[new("size",.5)]));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    [InlineData(double.MaxValue)] [InlineData(double.Epsilon)]
    public void InvalidNativeFloatsAreRejected(double scale) => Assert.Throws<ArgumentException>(()=>
        Dl1HumanAiScaleExperiments.Build(Source,"Character","control",[new("trial",scale)]));

    [Theory]
    [InlineData("control")] [InlineData("CONTROL")] [InlineData("bad\nname")]
    public void DuplicateOrControlCharacterNamesAreRejected(string name) => Assert.ThrowsAny<Exception>(()=>
        Dl1HumanAiScaleExperiments.Build(Source,"Character","control",[new(name,1)]));

    [Fact]
    public void AmbiguousAssignmentsAndNestedScaleAreRefused()
    {
        string duplicate=Source.Replace("SetField(\"m_ForcedBodyScaleMin\", \"1.1\");",
            "SetField(\"m_ForcedBodyScaleMin\", \"1.1\"); SetField(\"m_ForcedBodyScaleMin\", \"1.3\");",StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(()=>Dl1HumanAiScaleExperiments.Build(duplicate,"Character","control",[new("trial",1)]));
        string nested=Source.Replace("SetField(\"m_ForcedBodyScaleMin\", \"1.1\");",
            "if (condition) { SetField(\"m_ForcedBodyScaleMin\", \"1.1\"); }",StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(()=>Dl1HumanAiScaleExperiments.Build(nested,"Character","control",[new("trial",1)]));
    }

    [Theory]
    [InlineData("/* unterminated")]
    [InlineData("sub main() { PresetDef(\"broken)")]
    [InlineData("sub main() { ]")]
    public void MalformedSourcesAreRefused(string source) => Assert.Throws<InvalidDataException>(()=>
        Dl1HumanAiScaleExperiments.ListPresets(source,"Character"));

    [Fact]
    public void CultureAndQuotedNamesRoundTrip()
    {
        var previous=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
            var result=Dl1HumanAiScaleExperiments.Build(Source.Replace("\n","\r\n",StringComparison.Ordinal),"Character","control",[new("quoted \"trial\"",1.25)]);
            Assert.Contains("\"1.25\"",result.Source,StringComparison.Ordinal);
            Assert.Contains("quoted \"trial\"",Dl1HumanAiScaleExperiments.ListPresets(result.Source,"Character"));
            Assert.DoesNotContain('\n',result.Source.Replace("\r\n","",StringComparison.Ordinal));
        }
        finally{CultureInfo.CurrentCulture=previous;}
    }

    [Fact]
    public void ComputedPresetNamesCannotHideTrialNameCollisions()
    {
        string computed=Source.Replace("Preset(\"control\")","Preset(COMPUTED_NAME)",StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(()=>Dl1HumanAiScaleExperiments.Build(computed,"Character","control",[new("trial",1)]));
    }

    [Fact]
    public void GeneratedSourceIsBoundedBeforeAccumulatingManyLargeClones()
    {
        string large=Source.Replace("/* keep the comment and spacing */","/*"+new string('x',4*1024*1024)+"*/",StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(()=>Dl1HumanAiScaleExperiments.Build(large,"Character","control",[new("trial",1)]));
    }
}

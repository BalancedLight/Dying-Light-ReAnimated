using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class AnimationScriptSourceParserTests
{
    [Fact]
    public void CommentedOutIncludeIsNotTreatedAsLive()
    {
        // Stock anims_man_all.scr really does carry this line.
        const string source = """
            !include("events.def")
            //!include( "Anims_Player.scr")
            !include("Anims_Man_NPC.scr")
            """;

        AnimationScriptInclude[] includes =
            [.. AnimationScriptSourceParser.ParseIncludes(source)];

        Assert.Equal(2, includes.Length);
        Assert.Equal("events", includes[0].ResourceName);
        Assert.False(includes[0].IsAnimationScript);
        Assert.Equal("Anims_Man_NPC", includes[1].ResourceName);
        Assert.True(includes[1].IsAnimationScript);
    }

    [Fact]
    public void SeqTrackRowsParseWithAndWithoutEventBlocks()
    {
        const string source = """
            SeqTrack("Plain", "a.anm2", 0, 44, 30, 1, 0.5)
            SeqTrack("Evented", 	"b.anm2"	, 0, 38, 30, 1, 0.7)
            {
            	Event(13, SWIM_MOVE_IMPULSE, 0)
            	{
            		PlaySound23D(GameVolumeSource_SoundPlayer, "x.wav", 1, 3, "head", 1, [0,0,0], 0)
            	}
            }
            """;

        AnimationScriptSeqTrack[] tracks =
            [.. AnimationScriptSourceParser.ParseSeqTracks(source)];

        Assert.Equal(2, tracks.Length);
        Assert.Equal("Plain", tracks[0].Name);
        Assert.Equal("a.anm2", tracks[0].Anm2Name);
        Assert.Equal(44.0f, tracks[0].EndFrame.Number);
        Assert.Equal(30.0f, tracks[0].FramesPerSecond.Number);
        Assert.Equal(1.0f, tracks[0].Enabled.Number);
        Assert.Equal(0.5f, tracks[0].Blend.Number);
        Assert.False(tracks[0].HasEventBlock);

        // The bracketed vector argument inside the nested action block must not
        // be mistaken for the end of the SeqTrack argument list.
        Assert.Equal("Evented", tracks[1].Name);
        Assert.Equal(0.7f, tracks[1].Blend.Number);
        Assert.True(tracks[1].HasEventBlock);

        Assert.True(AnimationScriptSourceParser.ContainsEventBlocks(source));
    }

    [Fact]
    public void GeneratedScriptRoundTripsThroughTheParser()
    {
        string generated = CustomModelAnimationLibraryExporter
            .BuildLooseAnimationScript(
            [
                new ReAnimated.Codecs.Anm2.AnimationScrSequence(
                    "walk",
                    "walk.anm2",
                    0.0f,
                    44.0f,
                    30.0f),
            ]);

        AnimationScriptSeqTrack track = Assert.Single(
            AnimationScriptSourceParser.ParseSeqTracks(generated));

        Assert.Equal("walk", track.Name);
        Assert.Equal("walk.anm2", track.Anm2Name);
        Assert.Equal(0.0f, track.StartFrame.Number);
        Assert.Equal(44.0f, track.EndFrame.Number);
        Assert.Equal(30.0f, track.FramesPerSecond.Number);
        Assert.False(track.HasEventBlock);
        Assert.False(
            AnimationScriptSourceParser.ContainsEventBlocks(generated));
    }

    [Fact]
    public void NamedConstantArgumentsArePreservedInsteadOfRejected()
    {
        // m_fpp_hitreactions.scr blends on a constant from its .def includes.
        const string source =
            """
            SeqTrack("Bump", "a.anm2", 0, 44, 30, 1, CROWD_BUMP_BLENDIN_TIME)
            """;

        AnimationScriptSeqTrack track = Assert.Single(
            AnimationScriptSourceParser.ParseSeqTracks(source));

        Assert.True(track.Blend.IsSymbolic);
        Assert.Equal("CROWD_BUMP_BLENDIN_TIME", track.Blend.Text);
        Assert.Null(track.Blend.Number);
        Assert.Equal(44.0f, track.EndFrame.Number);
    }

    [Fact]
    public void GarbageArgumentIsStillRejected()
    {
        const string source =
            """
            SeqTrack("Bad", "a.anm2", 0, 44, 30, 1, 0.5.7)
            """;

        Assert.Throws<InvalidDataException>(() =>
            AnimationScriptSourceParser.ParseSeqTracks(source));
    }

    [Fact]
    public void SyntaxInsideAStringLiteralIsNotReadAsSyntax()
    {
        // Event arguments are opaque and may contain anything. A sound name
        // holding "SeqTrack(" must not be read as a malformed row, and one
        // holding "!include(...)" must not become a dependency.
        const string source = """
            SeqTrack("Real", "a.anm2", 0, 44, 30, 1, 0.5)
            {
            	Event(0, 0, 0)
            	{
            		PlaySound23D(Src, "SeqTrack( not real, honest", 1, 3, "head", 1, [0,0,0], 0)
            		PlaySound23D(Src, "!include(\"ghost.scr\")", 1, 3, "head", 1, [0,0,0], 0)
            	}
            }
            """;

        AnimationScriptSeqTrack track = Assert.Single(
            AnimationScriptSourceParser.ParseSeqTracks(source));
        Assert.Equal("Real", track.Name);
        Assert.True(track.HasEventBlock);

        Assert.Empty(AnimationScriptSourceParser.ParseIncludes(source));
    }

    [Fact]
    public void RealIncludesStillResolveAlongsideQuotedNoise()
    {
        const string source = """
            !include("events.def")
            SeqTrack("Real", "a.anm2", 0, 44, 30, 1, 0.5)
            {
            	Event(0, 0, 0)
            	{
            		PlaySound23D(Src, "!include(\"ghost.scr\")", 1, 3, "head", 1, [0,0,0], 0)
            	}
            }
            """;

        AnimationScriptInclude include = Assert.Single(
            AnimationScriptSourceParser.ParseIncludes(source));

        Assert.Equal("events", include.ResourceName);
    }

    [Fact]
    public void MalformedSeqTrackIsRejectedRatherThanDropped()
    {
        // A silently dropped row becomes a missing animation at deploy time.
        const string source = """
            SeqTrack("Truncated", "a.anm2", 0, 44)
            """;

        Assert.Throws<InvalidDataException>(() =>
            AnimationScriptSourceParser.ParseSeqTracks(source));
    }

    [Fact]
    public void EscapedQuotesSurviveStringParsing()
    {
        const string source =
            "SeqTrack( \"say \\\"hi\\\"\", \"a.anm2\", 0, 1, 30, 1, 0.5 )";

        AnimationScriptSeqTrack track = Assert.Single(
            AnimationScriptSourceParser.ParseSeqTracks(source));

        Assert.Equal("say \"hi\"", track.Name);
    }
}

using System.Collections.Immutable;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class AuthoredAnimationScriptTests
{
    private static PreparedCustomModelAnimationLibrary CreateLibrary(
        string looseScriptText,
        bool authored) =>
        new(
            "anims_dlr_authored",
            [
                new PreparedCustomModelAnimation(
                    "walk",
                    "walk.anm2",
                    [1, 2, 3],
                    45,
                    30,
                    "Walk",
                    new string('a', 64),
                    Dl1RootMotionMode.Recorded,
                    "bip01"),
            ],
            [
                new AnimationScrSequence("walk", "walk.anm2", 0.0f, 44.0f, 30.0f),
            ],
            looseScriptText,
            [])
        {
            IsAuthoredScript = authored,
        };

    [Fact]
    public void AuthoredScriptWithEventBlocksPassesTheInventoryCheck()
    {
        const string authored = """
            !include("events.def")
            SeqTrack( "walk", "walk.anm2", 0, 44, 30, 1, 0.5 )
            {
            	Event(13, SWIM_MOVE_IMPULSE, 0)
            }
            """;

        // The generated script would never look like this, so the old
        // byte-for-byte check would have rejected it outright.
        CustomModelAnimationLibraryExporter.ValidateLooseScriptCoversInventory(
            CreateLibrary(authored, authored: true));
    }

    [Fact]
    public void AuthoredScriptThatDropsASequenceIsRejected()
    {
        const string authored = """
            !include("events.def")
            SeqTrack( "sprint", "sprint.anm2", 0, 44, 30, 1, 0.5 )
            """;

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() =>
                CustomModelAnimationLibraryExporter
                    .ValidateLooseScriptCoversInventory(
                        CreateLibrary(authored, authored: true)));

        Assert.Contains("walk", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoredScriptWithMismatchedTimingIsRejected()
    {
        const string authored = """
            SeqTrack( "walk", "walk.anm2", 0, 12, 30, 1, 0.5 )
            """;

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() =>
                CustomModelAnimationLibraryExporter
                    .ValidateLooseScriptCoversInventory(
                        CreateLibrary(authored, authored: true)));

        Assert.Contains(
            "end frame",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoredScriptWithASymbolicTimingFieldIsAccepted()
    {
        // The constant comes from a .def include this tool does not resolve,
        // so it is authored intent rather than a detectable mismatch.
        const string authored = """
            SeqTrack( "walk", "walk.anm2", 0, 44, 30, 1, CROWD_BUMP_BLENDIN_TIME )
            """;

        CustomModelAnimationLibraryExporter.ValidateLooseScriptCoversInventory(
            CreateLibrary(authored, authored: true));
    }

    [Fact]
    public void AuthoredScriptNamingTheWrongAnm2IsRejected()
    {
        const string authored = """
            SeqTrack( "walk", "run.anm2", 0, 44, 30, 1, 0.5 )
            """;

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() =>
                CustomModelAnimationLibraryExporter
                    .ValidateLooseScriptCoversInventory(
                        CreateLibrary(authored, authored: true)));

        Assert.Contains(
            "run.anm2",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedLibrariesKeepTheirByteForByteCheck()
    {
        PreparedCustomModelAnimationLibrary tampered = CreateLibrary(
            "SeqTrack( \"walk\", \"walk.anm2\", 0, 44, 30, 1, 0.5 )\n",
            authored: false);

        Assert.Throws<InvalidDataException>(() =>
            CustomModelAnimationLibraryExporter
                .ValidateLooseScriptCoversInventory(tampered));

        PreparedCustomModelAnimationLibrary generated = CreateLibrary(
            CustomModelAnimationLibraryExporter.BuildLooseAnimationScript(
            [
                new AnimationScrSequence(
                    "walk",
                    "walk.anm2",
                    0.0f,
                    44.0f,
                    30.0f),
            ]),
            authored: false);

        CustomModelAnimationLibraryExporter.ValidateLooseScriptCoversInventory(
            generated);
    }

    [Fact]
    public void AuthoredScriptTextRoundTripsThroughTheProjectFile()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ReAnimated-AuthoredScr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const string authored =
                "!include(\"events.def\")\nSeqTrack( \"walk\", \"walk.anm2\", 0, 44, 30, 1, 0.5 )\n{\n\tEvent(13, SWIM_MOVE_IMPULSE, 0)\n}\n";
            Guid libraryId = Guid.NewGuid();
            DlraProject project = DlraProject.Create("Authored") with
            {
                AnimationLibraries =
                [
                    new ProjectAnimationLibrary
                    {
                        Id = libraryId,
                        ResourceName = "anims_dlr_authored",
                        DisplayName = "Authored",
                        AuthoredScriptText = authored,
                    },
                ],
            };
            string path = Path.Combine(directory, "authored.dlraproj");
            ProjectSerializer.SaveAtomic(project, path);

            DlraProject reloaded = ProjectSerializer.Load(path);

            ProjectAnimationLibrary library = Assert.Single(
                reloaded.AnimationLibraries,
                candidate => candidate.Id == libraryId);
            Assert.Equal(authored, library.AuthoredScriptText);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BlankAuthoredScriptIsRejectedInsteadOfSilentlyEmptying()
    {
        DlraProject project = DlraProject.Create("Blank authored") with
        {
            AnimationLibraries =
            [
                new ProjectAnimationLibrary
                {
                    Id = Guid.NewGuid(),
                    ResourceName = "anims_dlr_blank",
                    DisplayName = "Blank",
                    AuthoredScriptText = "   ",
                },
            ],
        };

        Assert.Throws<ArgumentException>(project.Validate);
    }

    [Fact]
    public void AbsentAuthoredScriptLeavesTheLibraryGenerated()
    {
        DlraProject project = DlraProject.Create("Generated") with
        {
            AnimationLibraries =
            [
                new ProjectAnimationLibrary
                {
                    Id = Guid.NewGuid(),
                    ResourceName = "anims_dlr_generated",
                    DisplayName = "Generated",
                },
            ],
        };

        project.Validate();

        Assert.Null(Assert.Single(project.AnimationLibraries)
            .AuthoredScriptText);
    }
}

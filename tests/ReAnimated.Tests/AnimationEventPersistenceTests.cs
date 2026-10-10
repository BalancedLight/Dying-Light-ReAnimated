using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class AnimationEventPersistenceTests
{
    [Fact]
    public void ProjectRoundTripRetainsSequenceEventsAndActionOrder()
    {
        string directory = TemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "animation-events.dlraproj");
            ProjectAnimationLibrary library = Library([Sequence()]);
            ProjectSerializer.SaveAtomic(DlraProject.Create("Generic event project") with { AnimationLibraries = [library] }, path);

            ProjectAnimationLibrary reopened = Assert.Single(ProjectSerializer.Load(path).AnimationLibraries);
            AnimationEvent[] events = Assert.Single(reopened.SequenceUses).Events.ToArray();
            Assert.Equal([1012, 1013], events.Select(e => e.EventId).ToArray());
            Assert.Equal(["PlaySound", "HideElement"], events[0].Actions.Select(a => a.Keyword).ToArray());
            Assert.Equal("foot.wav", events[0].Actions[0].Arguments[0].Value);
            Assert.Equal(events[0].Id, reopened.SequenceUses[0].Events[0].Id);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ProjectSchemaThreeWithoutSequenceUsesMigratesToCurrentDefaults()
    {
        string directory = TemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "schema-three.dlraproj");
            ProjectSerializer.SaveAtomic(DlraProject.Create("Generic event migration") with { AnimationLibraries = [Library([Sequence()])] }, path);
            JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document["schemaVersion"] = 3;
            JsonArray libraries = document["animationLibraries"]!.AsArray();
            libraries[0]!.AsObject().Remove("sequenceUses");
            File.WriteAllText(path, document.ToJsonString());

            DlraProject migrated = ProjectSerializer.Load(path);

            Assert.Equal(DlraProject.CurrentSchemaVersion, migrated.SchemaVersion);
            Assert.Empty(Assert.Single(migrated.AnimationLibraries).SequenceUses);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CustomModelSchemaTenRoundTripsEventsAndSchemaNineDefaultsThemToEmpty()
    {
        CustomModelPackage package = Package(Sequence());
        ImmutableArray<byte> currentBytes = CustomModelPackageSerializer.Serialize(package);
        string directory = TemporaryDirectory();
        try
        {
            string currentPath = Path.Combine(directory, "current.dlrmodel");
            File.WriteAllBytes(currentPath, currentBytes.ToArray());
            CustomModelDocument reopened = CustomModelPackageSerializer.Load(currentPath).Document;
            Assert.Equal(CustomModelDocument.CurrentSchemaVersion, reopened.SchemaVersion);
            Assert.Equal(2, Assert.Single(reopened.SequenceUses).Events.Length);

            byte[] legacyBytes = RewriteManifest(currentBytes.AsSpan(), json =>
            {
                json["schemaVersion"] = 9;
                json.Remove("sequenceUses");
            });
            string legacyPath = Path.Combine(directory, "schema-nine.dlrmodel");
            File.WriteAllBytes(legacyPath, legacyBytes);
            CustomModelDocument migrated = CustomModelPackageSerializer.Load(legacyPath).Document;
            Assert.Equal(CustomModelDocument.CurrentSchemaVersion, migrated.SchemaVersion);
            Assert.Empty(migrated.SequenceUses);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SourceWriterRetainsDuplicateSameTimeEventsAndReordersActionsWithoutDroppingComments()
    {
        const string source = "SeqTrack(\"generic_actions\", \"generic_actions.anm2\", 0, 20, 30, 1, 0.5)\n{\n    Event(4, 1012, -1)\n    {\n        FirstAction(\"one\") // keep first note\n        SecondAction(\"two\")\n    }\n    Event(4, 1012, -1)\n}\n";
        var document = ReAnimated.Codecs.AnimationScripts.AnimationScriptTextCodec.Read(source);
        AnimationSequenceUse sequence = Assert.Single(document.Sequences);
        AnimationEvent first = sequence.Events[0];
        AnimationSequenceUse edited = sequence with
        {
            Events = sequence.Events.SetItem(0, first with { Actions = [.. first.Actions.Reverse()] }),
        };

        string written = ReAnimated.Codecs.AnimationScripts.AnimationScriptTextCodec.Write(document with { Sequences = [edited] });

        Assert.Equal(2, Assert.Single(ReAnimated.Codecs.AnimationScripts.AnimationScriptTextCodec.Read(written).Sequences).Events.Length);
        Assert.True(written.IndexOf("SecondAction", StringComparison.Ordinal) < written.IndexOf("FirstAction", StringComparison.Ordinal));
        Assert.Contains("// keep first note", written);
    }

    [Fact]
    public void SourceWriterCanDropAnActionWhileKeepingItsCommentAndFollowingAction()
    {
        const string source = "SeqTrack(\"generic_actions\", \"generic_actions.anm2\", 0, 20, 30, 1, 0.5)\n{\n    Event(4, 1012, -1)\n    {\n        FirstAction(\"one\") // keep removed-action note\n        SecondAction(\"two\")\n    }\n}\n";
        var document = ReAnimated.Codecs.AnimationScripts.AnimationScriptTextCodec.Read(source);
        AnimationSequenceUse sequence = Assert.Single(document.Sequences);
        AnimationEvent item = Assert.Single(sequence.Events);
        AnimationSequenceUse edited = sequence with
        {
            Events = [item with { Actions = [item.Actions[1]] }],
        };

        string written = ReAnimated.Codecs.AnimationScripts.AnimationScriptTextCodec.Write(document with { Sequences = [edited] });

        Assert.DoesNotContain("FirstAction", written);
        Assert.Contains("// keep removed-action note", written);
        Assert.Contains("SecondAction(\"two\")", written);
    }

    private static ProjectAnimationLibrary Library(ImmutableArray<AnimationSequenceUse> sequences) => new()
    {
        ResourceName = "generic_events",
        DisplayName = "Generic events",
        AuthoredScriptText = "SeqTrack(\"generic_walk\", \"generic_walk.anm2\", 0, 20, 30, 1, 0.5)",
        SequenceUses = sequences,
    };

    private static AnimationSequenceUse Sequence() => new()
    {
        Name = "generic_walk",
        Anm2Name = "generic_walk.anm2",
        SourceStartFrame = 0,
        SourceEndFrame = 20,
        FPS = 30,
        WeightMode = 1,
        WeightTime = .5,
        Events =
        [
            new AnimationEvent
            {
                LocalFrame = 4,
                EventId = 1012,
                IdExpression = "VIS_EVENT_LEFT_FOOT_LAND",
                RequiredSlot = -1,
                SlotExpression = "-1",
                Actions =
                [
                    new AnimationEventAction { Keyword = "PlaySound", Arguments = [new AnimationEventArgument { Text = "\"foot.wav\"", Value = "foot.wav" }] },
                    new AnimationEventAction { Keyword = "HideElement", Arguments = [new AnimationEventArgument { Text = "\"left_glove\"", Value = "left_glove" }] },
                ],
            },
            new AnimationEvent { LocalFrame = 4, EventId = 1013, IdExpression = "VIS_EVENT_LEFT_FOOT_LIFT", RequiredSlot = -1, SlotExpression = "-1" },
        ],
    };

    private static CustomModelPackage Package(AnimationSequenceUse sequence)
    {
        ImmutableArray<byte> source = [1, 2, 3, 4, 5];
        string hash = Convert.ToHexStringLower(SHA256.HashData(source.AsSpan()));
        string[] names = ["root", "secondary_anchor", "secondary_tip", "weapon", "camera"];
        var document = new CustomModelDocument
        {
            SequenceUses = [sequence],
            Source = new() { OriginalFileName = "synthetic.fbx", ContentSha256 = hash, FbxVersion = 7400 },
            RigSignature = hash,
            MorphSignature = hash,
            Bones = names.Select((name, index) => new CustomModelBone { Name = name, Index = index, FbxObjectId = index + 1 }).ToImmutableArray(),
            MorphChannels = [new() { Name = "eye_wide", Index = 0, BlendShapeChannelObjectId = 100, ShapeObjectId = 101, GeometryObjectIds = [102] }],
        };
        return new(document, source, ImmutableDictionary<string, ImmutableArray<byte>>.Empty);
    }

    private static byte[] RewriteManifest(ReadOnlySpan<byte> bytes, Action<JsonObject> edit)
    {
        using var source = new ZipArchive(new MemoryStream(bytes.ToArray()), ZipArchiveMode.Read);
        using var buffer = new MemoryStream();
        using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using Stream input = entry.Open();
                using Stream target = output.CreateEntry(entry.FullName).Open();
                if (entry.FullName == CustomModelPackage.ManifestEntryPath)
                {
                    JsonObject json = JsonNode.Parse(input)!.AsObject();
                    edit(json);
                    JsonSerializer.Serialize(target, json);
                }
                else input.CopyTo(target);
            }
        }
        return buffer.ToArray();
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "reanimated-animation-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

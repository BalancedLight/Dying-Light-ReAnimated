using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using ReAnimated.Core.Domain;

namespace ReAnimated.Codecs.AnimationScripts;

/// <summary>A lossless parsed animation script and its editable SeqTracks.</summary>
public sealed record AnimationScriptTextDocument
{
    public string OriginalText { get; init; } = string.Empty;
    public ImmutableArray<AnimationSequenceUse> Sequences { get; init; } = [];
    internal ImmutableArray<SequenceNode> SequenceNodes { get; init; } = [];
}

internal sealed record SequenceNode(
    Guid Id,
    AnimationScriptSourceSpan ArgumentSpan,
    AnimationScriptSourceSpan? BlockInteriorSpan,
    AnimationSequenceUse Baseline,
    ImmutableArray<EventNode> Events);

internal sealed record EventNode(
    Guid Id,
    AnimationScriptSourceSpan NameSpan,
    AnimationScriptSourceSpan ArgumentSpan,
    AnimationScriptSourceSpan? BlockInteriorSpan,
    AnimationEvent Baseline,
    ImmutableArray<ActionNode> Actions);

internal sealed record ActionNode(Guid Id, AnimationScriptSourceSpan NameSpan, AnimationScriptSourceSpan ArgumentSpan, AnimationEventAction Baseline);

/// <summary>Reads and writes loose .scr event source while retaining unrelated text.</summary>
public static class AnimationScriptTextCodec
{
    private const int MaximumSourceLength = 8 * 1024 * 1024;
    private static readonly string[] SeqTrackNames = ["SeqTrack"];

    public static AnimationScriptTextDocument Read(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaximumSourceLength)
            throw new InvalidDataException("An animation script source exceeds the supported size.");

        string scan = MaskComments(source);
        ImmutableArray<AnimationSequenceUse>.Builder sequences = ImmutableArray.CreateBuilder<AnimationSequenceUse>();
        ImmutableArray<SequenceNode>.Builder nodes = ImmutableArray.CreateBuilder<SequenceNode>();
        foreach (Call call in FindCalls(scan, 0, source.Length, SeqTrackNames))
        {
            ImmutableArray<Span> args = SplitArguments(scan, call.Arguments);
            if (args.Length != 7)
                throw new InvalidDataException($"A SeqTrack row declares {args.Length} arguments; 7 are required.");
            string name = ReadString(Slice(source, args[0]));
            string anm2 = ReadString(Slice(source, args[1]));
            string startExpression = Clean(Slice(source, args[2]));
            string endExpression = Clean(Slice(source, args[3]));
            string fpsExpression = Clean(Slice(source, args[4]));
            string weightModeExpression = Clean(Slice(source, args[5]));
            string weightTimeExpression = Clean(Slice(source, args[6]));
            AnimationScriptSourceSpan? block = FindFollowingBlock(scan, call.End);
            ImmutableArray<AnimationEvent>.Builder events = ImmutableArray.CreateBuilder<AnimationEvent>();
            ImmutableArray<EventNode>.Builder eventNodes = ImmutableArray.CreateBuilder<EventNode>();
            if (block is { } blockSpan)
            {
                foreach (Call eventCall in FindCalls(scan, blockSpan.Start, blockSpan.End, ["Event", "MustSendEvent"]))
                {
                    ImmutableArray<Span> eventArgs = SplitArguments(scan, eventCall.Arguments);
                    if (eventArgs.Length < 2 || eventArgs.Length > 3)
                        throw new InvalidDataException("An Event statement must have a frame, ID, and optional slot.");
                    string frameExpression = Clean(Slice(source, eventArgs[0]));
                    string idExpression = Clean(Slice(source, eventArgs[1]));
                    string slotExpression = eventArgs.Length == 3 ? Clean(Slice(source, eventArgs[2])) : "";
                    double localFrame = ParseNumber(frameExpression, 0);
                    int? numericId = int.TryParse(idExpression, NumberStyles.Integer, CultureInfo.InvariantCulture, out int eid) ? eid : null;
                    short? slot = short.TryParse(slotExpression, NumberStyles.Integer, CultureInfo.InvariantCulture, out short eslot) ? eslot : null;
                    AnimationScriptSourceSpan? eventBlock = FindFollowingBlock(scan, eventCall.End);
                    ImmutableArray<AnimationEventAction>.Builder actions = ImmutableArray.CreateBuilder<AnimationEventAction>();
                    ImmutableArray<ActionNode>.Builder actionNodes = ImmutableArray.CreateBuilder<ActionNode>();
                    if (eventBlock is { } eventBlockSpan)
                    {
                        foreach (Call actionCall in FindCalls(scan, eventBlockSpan.Start, eventBlockSpan.End, null))
                        {
                            if (actionCall.Name.Equals("Event", StringComparison.OrdinalIgnoreCase) || actionCall.Name.Equals("MustSendEvent", StringComparison.OrdinalIgnoreCase))
                                continue;
                            ImmutableArray<Span> actionArgs = SplitArguments(source, actionCall.Arguments);
                            ImmutableArray<AnimationEventArgument> parsedArgs = actionArgs
                                .Select(s => new AnimationEventArgument { Text = Slice(source, s), Value = TryReadString(Slice(source, s)), SourceSpan = new AnimationScriptSourceSpan(s.Start, s.Length) })
                                .ToImmutableArray();
                            AnimationScriptSourceSpan actionSpan = new(actionCall.Start, actionCall.End - actionCall.Start);
                            AnimationEventAction action = new() { Keyword = actionCall.Name, Arguments = parsedArgs, SourceSpan = actionSpan, OriginalSource = source[actionSpan.Start..actionSpan.End] };
                            actions.Add(action);
                            actionNodes.Add(new ActionNode(action.Id, new AnimationScriptSourceSpan(actionCall.Start, actionCall.Name.Length), ToSourceSpan(actionCall.Arguments), action));
                        }
                    }
                    AnimationScriptSourceSpan evSpan = new(eventCall.Start, eventBlock is { } fullEventBlock ? fullEventBlock.End + 1 - eventCall.Start : eventCall.End - eventCall.Start);
                    AnimationEvent parsedEvent = new()
                    {
                        LocalFrame = localFrame,
                        FrameExpression = IsNumeric(frameExpression) ? null : frameExpression,
                        EventId = numericId ?? Dl1AnimationEventCatalog.FindEvent(idExpression)?.Id,
                        IdExpression = idExpression,
                        RequiredSlot = slot,
                        SlotExpression = slotExpression,
                        Delivery = eventCall.Name.Equals("MustSendEvent", StringComparison.OrdinalIgnoreCase) ? AnimationEventDelivery.MustSendEvent : AnimationEventDelivery.Event,
                        Actions = actions.ToImmutable(),
                        SourceSpan = evSpan,
                        OriginalSource = source[evSpan.Start..evSpan.End],
                    };
                    events.Add(parsedEvent);
                    eventNodes.Add(new EventNode(parsedEvent.Id, new AnimationScriptSourceSpan(eventCall.Start, eventCall.Name.Length), ToSourceSpan(eventCall.Arguments), eventBlock, parsedEvent, actionNodes.ToImmutable()));
                }
            }
            AnimationScriptSourceSpan seqSpan = new(call.Start, call.End - call.Start);
            AnimationSequenceUse sequence = new()
            {
                Name = name,
                Anm2Name = anm2,
                SourceStartFrame = ParseNumber(startExpression, 0),
                SourceEndFrame = ParseNumber(endExpression, 0),
                FPS = ParseNumber(fpsExpression, 0),
                WeightMode = (int)ParseNumber(weightModeExpression, 0),
                WeightTime = ParseNumber(weightTimeExpression, 0),
                SourceStartFrameExpression = IsNumeric(startExpression) ? null : startExpression,
                SourceEndFrameExpression = IsNumeric(endExpression) ? null : endExpression,
                FpsExpression = IsNumeric(fpsExpression) ? null : fpsExpression,
                WeightModeExpression = IsNumeric(weightModeExpression) ? null : weightModeExpression,
                WeightTimeExpression = IsNumeric(weightTimeExpression) ? null : weightTimeExpression,
                Events = events.ToImmutable(),
                SourceSpan = new AnimationScriptSourceSpan(call.Start, block is { } seqBlock ? seqBlock.End + 1 - call.Start : call.End - call.Start),
                OriginalSource = source[call.Start..(block is { } fullSeqBlock ? fullSeqBlock.End + 1 : call.End)],
            };
            sequences.Add(sequence);
            nodes.Add(new SequenceNode(sequence.Id, ToSourceSpan(call.Arguments), block, sequence, eventNodes.ToImmutable()));
        }
        return new AnimationScriptTextDocument { OriginalText = source, Sequences = sequences.ToImmutable(), SequenceNodes = nodes.ToImmutable() };
    }

    public static string Write(AnimationScriptTextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Sequences.Length == document.SequenceNodes.Length &&
            document.Sequences.Zip(document.SequenceNodes).All(pair => pair.First == pair.Second.Baseline))
            return document.OriginalText;

        if (document.Sequences.SelectMany(s => s.Events).Any(e =>
                e.RawActionReference is { } reference && reference != uint.MaxValue && e.Actions.IsDefaultOrEmpty))
            throw new InvalidOperationException("The source script cannot represent an unresolved compiled action reference.");

        List<Edit> edits = [];
        Dictionary<Guid, SequenceNode> nodes = document.SequenceNodes.ToDictionary(n => n.Id);
        foreach (AnimationSequenceUse sequence in document.Sequences)
        {
            if (!nodes.TryGetValue(sequence.Id, out SequenceNode? node))
            {
                edits.Add(new Edit(new Span(document.OriginalText.Length, 0), "\n" + RenderSequence(sequence) + "\n"));
                continue;
            }
            if (sequence == node.Baseline)
                continue;

            if (sequence.Name != node.Baseline.Name || sequence.Anm2Name != node.Baseline.Anm2Name ||
                sequence.SourceStartFrame != node.Baseline.SourceStartFrame || sequence.SourceEndFrame != node.Baseline.SourceEndFrame ||
                sequence.FPS != node.Baseline.FPS || sequence.WeightMode != node.Baseline.WeightMode || sequence.WeightTime != node.Baseline.WeightTime)
            {
                ImmutableArray<Span> fields = SplitArguments(MaskComments(document.OriginalText), ToSpan(node.ArgumentSpan));
                string[] values = [Quote(sequence.Name), Quote(sequence.Anm2Name), Field(sequence.SourceStartFrame, sequence.SourceStartFrameExpression, node.Baseline.SourceStartFrame), Field(sequence.SourceEndFrame, sequence.SourceEndFrameExpression, node.Baseline.SourceEndFrame), Field(sequence.FPS, sequence.FpsExpression, node.Baseline.FPS), sequence.WeightMode != node.Baseline.WeightMode ? Format(sequence.WeightMode) : sequence.WeightModeExpression ?? Format(sequence.WeightMode), Field(sequence.WeightTime, sequence.WeightTimeExpression, node.Baseline.WeightTime)];
                for (int i = 0; i < Math.Min(fields.Length, values.Length); i++)
                    if (values[i] != Clean(Slice(document.OriginalText, fields[i]))) edits.Add(new Edit(fields[i], values[i]));
            }

            Dictionary<Guid, EventNode> eventNodes = node.Events.ToDictionary(e => e.Id);
            foreach (EventNode removed in node.Events.Where(e => sequence.Events.All(current => current.Id != e.Id)))
            {
                if (removed.Baseline.SourceSpan is { } removedSpan)
                    edits.Add(new Edit(ToSpan(removedSpan), PreserveComments(document.OriginalText.Substring(removedSpan.Start, removedSpan.Length))));
            }
            List<string> appendedEvents = [];
            foreach (AnimationEvent current in sequence.Events)
            {
                if (!eventNodes.TryGetValue(current.Id, out EventNode? eventNode))
                {
                    appendedEvents.Add(RenderEvent(current));
                    continue;
                }
                if (current == eventNode.Baseline)
                    continue;
                if (current.Delivery != eventNode.Baseline.Delivery)
                    edits.Add(new Edit(ToSpan(eventNode.NameSpan), current.Delivery == AnimationEventDelivery.MustSendEvent ? "MustSendEvent" : "Event"));
                ImmutableArray<Span> args = SplitArguments(MaskComments(document.OriginalText), ToSpan(eventNode.ArgumentSpan));
                string[] values = [current.LocalFrame == eventNode.Baseline.LocalFrame ? current.FrameExpression ?? Format(current.LocalFrame) : Format(current.LocalFrame), current.IdExpression == eventNode.Baseline.IdExpression && current.EventId != eventNode.Baseline.EventId && current.EventId is { } numericId ? Format(numericId) : current.IdExpression, current.SlotExpression == eventNode.Baseline.SlotExpression && current.RequiredSlot != eventNode.Baseline.RequiredSlot && current.RequiredSlot is { } numericSlot ? Format(numericSlot) : current.SlotExpression];
                for (int i = 0; i < Math.Min(args.Length, values.Length); i++)
                    if (values[i] != Clean(Slice(document.OriginalText, args[i]))) edits.Add(new Edit(args[i], values[i]));
                if (args.Length == 2 && current.RequiredSlot is not null && eventNode.Baseline.RequiredSlot is null)
                    edits.Add(new Edit(new Span(eventNode.ArgumentSpan.End, 0), ", " + current.SlotExpression));
                Dictionary<Guid, ActionNode> actionNodes = eventNode.Actions.ToDictionary(a => a.Id);
                ActionNode[] sourceActionOrder = eventNode.Actions
                    .OrderBy(a => a.Baseline.SourceSpan?.Start ?? int.MaxValue)
                    .ToArray();
                AnimationEventAction[] desiredActions = current.Actions.ToArray();
                for (int actionIndex = 0; actionIndex < Math.Min(sourceActionOrder.Length, desiredActions.Length); actionIndex++)
                {
                    ActionNode slot = sourceActionOrder[actionIndex];
                    if (slot.Baseline.SourceSpan is not { } sourceSpan) continue;
                    AnimationEventAction desired = desiredActions[actionIndex];
                    ActionNode? originalNode = actionNodes.GetValueOrDefault(desired.Id);
                    string rendered = originalNode is not null && desired == originalNode.Baseline
                        ? originalNode.Baseline.SourceSpan is { } originalSpan
                            ? document.OriginalText.Substring(originalSpan.Start, originalSpan.Length)
                            : RenderAction(desired)
                        : RenderAction(desired);
                    if (rendered != document.OriginalText.Substring(sourceSpan.Start, sourceSpan.Length))
                        edits.Add(new Edit(ToSpan(sourceSpan), rendered));
                }
                foreach (ActionNode removed in sourceActionOrder.Skip(desiredActions.Length))
                {
                    if (removed.Baseline.SourceSpan is { } removedSpan)
                        edits.Add(new Edit(ToSpan(removedSpan), PreserveComments(document.OriginalText.Substring(removedSpan.Start, removedSpan.Length))));
                }
                if (desiredActions.Length > sourceActionOrder.Length && eventNode.BlockInteriorSpan is { } actionBlock)
                {
                    string indent = DetectIndent(document.OriginalText, eventNode.Baseline.SourceSpan?.Start ?? actionBlock.Start) + "\t";
                    edits.Add(new Edit(new Span(actionBlock.End, 0), "\n" + string.Join("\n", desiredActions.Skip(sourceActionOrder.Length).Select(a => indent + RenderAction(a))) + "\n"));
                }
                else if (desiredActions.Length > sourceActionOrder.Length)
                {
                    int insertAt = eventNode.ArgumentSpan.End + 1;
                    string indent = DetectIndent(document.OriginalText, eventNode.Baseline.SourceSpan?.Start ?? insertAt) + "\t";
                    edits.Add(new Edit(new Span(insertAt, 0), "\n{\n" + string.Join("\n", desiredActions.Skip(sourceActionOrder.Length).Select(a => indent + RenderAction(a))) + "\n" + DetectIndent(document.OriginalText, eventNode.Baseline.SourceSpan?.Start ?? insertAt) + "}"));
                }
            }
            if (appendedEvents.Count > 0)
            {
                if (node.BlockInteriorSpan is { } block)
                {
                    string indent = DetectIndent(document.OriginalText, node.Baseline.SourceSpan?.Start ?? block.Start) + "\t";
                    edits.Add(new Edit(new Span(block.End, 0), "\n" + string.Join("\n", appendedEvents.Select(e => indent + e.Replace("\n", "\n" + indent, StringComparison.Ordinal))) + "\n"));
                }
                else
                {
                    int insertAt = node.ArgumentSpan.End + 1;
                    string indent = "\t";
                    edits.Add(new Edit(new Span(insertAt, 0), "\n{\n" + string.Join("\n", appendedEvents.Select(e => indent + e.Replace("\n", "\n" + indent, StringComparison.Ordinal))) + "\n}"));
                }
            }
        }
        foreach (SequenceNode removed in document.SequenceNodes.Where(n => document.Sequences.All(current => current.Id != n.Id)))
        {
            if (removed.Baseline.SourceSpan is { } removedSpan)
                edits.Add(new Edit(ToSpan(removedSpan), PreserveComments(document.OriginalText.Substring(removedSpan.Start, removedSpan.Length))));
        }
        return Apply(document.OriginalText, edits);
    }

    private static string Apply(string source, List<Edit> edits)
    {
        StringBuilder result = new(source);
        foreach (Edit edit in edits.OrderByDescending(e => e.Span.Start))
            result.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.Text);
        return result.ToString();
    }

    private static IEnumerable<Call> FindCalls(string source, int start, int end, string[]? only)
    {
        int index = start;
        while (index < end)
        {
            if (source[index] == '"')
            {
                index++;
                while (index < end)
                {
                    if (source[index] == '\\') { index += Math.Min(2, end - index); continue; }
                    if (source[index++] == '"') break;
                }
                continue;
            }
            if (!(char.IsLetter(source[index]) || source[index] == '_')) { index++; continue; }
            int nameStart = index++;
            while (index < end && (char.IsLetterOrDigit(source[index]) || source[index] == '_')) index++;
            string name = source[nameStart..index];
            if (only is not null && !only.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            int open = SkipWhitespace(source, index, end);
            if (open >= end || source[open] != '(') continue;
            int close = FindPair(source, open, '(', ')', end);
            if (close < 0) throw new InvalidDataException($"The {name} call has an unterminated argument list.");
            int callEnd = close + 1;
            int brace = SkipWhitespace(source, callEnd, end);
            if (brace < end && source[brace] == '{')
            {
                int braceEnd = FindPair(source, brace, '{', '}', end);
                if (braceEnd >= 0) callEnd = braceEnd + 1;
            }
            yield return new Call(name, nameStart, close + 1, new Span(open + 1, close - open - 1));
            index = callEnd;
        }
    }

    private static AnimationScriptSourceSpan? FindFollowingBlock(string source, int start)
    {
        int open = SkipWhitespace(source, start, source.Length);
        if (open >= source.Length || source[open] != '{') return null;
        int close = FindPair(source, open, '{', '}', source.Length);
        return close < 0 ? null : new AnimationScriptSourceSpan(open + 1, close - open - 1);
    }

    private static int FindPair(string source, int open, char left, char right, int limit)
    {
        int depth = 0; bool quote = false;
        for (int i = open; i < limit; i++)
        {
            char c = source[i];
            if (c == '\0') continue;
            if (quote) { if (c == '\\') { i++; continue; } if (c == '"') quote = false; continue; }
            if (c == '"') { quote = true; continue; }
            if (c == left) depth++;
            else if (c == right && --depth == 0) return i;
        }
        return -1;
    }

    private static ImmutableArray<Span> SplitArguments(string source, Span span)
    {
        ImmutableArray<Span>.Builder result = ImmutableArray.CreateBuilder<Span>();
        int start = span.Start, round = 0, square = 0, brace = 0; bool quote = false;
        for (int i = span.Start; i < span.End; i++)
        {
            char c = source[i];
            if (quote) { if (c == '\\') { i++; continue; } if (c == '"') quote = false; continue; }
            if (c == '"') { quote = true; continue; }
            switch (c)
            {
                case '(': round++; break; case ')': round--; break;
                case '[': square++; break; case ']': square--; break;
                case '{': brace++; break; case '}': brace--; break;
                case ',' when round == 0 && square == 0 && brace == 0: result.Add(Trim(source, start, i)); start = i + 1; break;
            }
        }
        if (start < span.End || result.Count > 0) result.Add(Trim(source, start, span.End));
        return result.ToImmutable();
    }

    private static Span Trim(string source, int start, int end) { while (start < end && (char.IsWhiteSpace(source[start]) || source[start] == '\0')) start++; while (end > start && (char.IsWhiteSpace(source[end - 1]) || source[end - 1] == '\0')) end--; return new Span(start, end - start); }
    private static string Slice(string source, Span span) => source.Substring(span.Start, span.Length);
    private static string Clean(string text) => ReAnimated.Codecs.Models.AnimationScriptSourceParser.StripComments(text).Trim();
    private static int SkipWhitespace(string source, int index, int end) { while (index < end && (char.IsWhiteSpace(source[index]) || source[index] == '\0')) index++; return index; }
    private static string ReadString(string text) => TryReadString(text) ?? throw new InvalidDataException("A SeqTrack name and animation name must be quoted strings.");
    private static string? TryReadString(string text) { text = Clean(text); if (text.Length < 2 || text[0] != '"' || text[^1] != '"') return null; return text[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal); }
    private static double ParseNumber(string text, double fallback) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : fallback;
    private static bool IsNumeric(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value);
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static string Format(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static string FormatPrecise(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string Field(double value, string? expression, double baseline) => value == baseline ? expression ?? FormatPrecise(value) : FormatPrecise(value);
    private static string HeaderNumber(double value, string? expression)
    {
        if (expression is null) return FormatPrecise(value);
        if (double.TryParse(expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric))
            return double.IsFinite(numeric) && numeric == value ? expression : FormatPrecise(value);
        return expression;
    }

    private static string HeaderInteger(int value, string? expression)
    {
        if (expression is null) return value.ToString(CultureInfo.InvariantCulture);
        if (int.TryParse(expression, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            return numeric == value ? expression : value.ToString(CultureInfo.InvariantCulture);
        return expression;
    }
    private static Span ToSpan(AnimationScriptSourceSpan span) => new(span.Start, span.Length);
    private static AnimationScriptSourceSpan ToSourceSpan(Span span) => new(span.Start, span.Length);

    private static string MaskComments(string source)
    {
        char[] chars = source.ToCharArray(); bool quote = false;
        for (int i = 0; i < chars.Length; i++)
        {
            if (quote) { if (chars[i] == '\\') { i++; continue; } if (chars[i] == '"') quote = false; continue; }
            if (chars[i] == '"') { quote = true; continue; }
            if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '/') { while (i < chars.Length && chars[i] != '\n') chars[i++] = '\0'; i--; continue; }
            if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*') { chars[i++] = '\0'; chars[i++] = '\0'; while (i + 1 < chars.Length && !(chars[i] == '*' && chars[i + 1] == '/')) chars[i++] = chars[i] == '\n' ? '\n' : '\0'; if (i + 1 < chars.Length) { chars[i++] = '\0'; chars[i] = '\0'; } }
        }
        return new string(chars);
    }

    private static string PreserveComments(string text)
    {
        char[] output = text.ToCharArray(); bool quote = false;
        for (int i = 0; i < output.Length; i++)
        {
            char c = output[i];
            if (quote) { if (c == '\\') { output[i] = ' '; if (i + 1 < output.Length) output[++i] = ' '; continue; } if (c == '"') quote = false; output[i] = c == '\n' || c == '\r' ? c : ' '; continue; }
            if (c == '"') { quote = true; output[i] = ' '; continue; }
            if (c == '/' && i + 1 < output.Length && output[i + 1] == '/') { while (i < output.Length && output[i] != '\n') { if (output[i] != '\r') output[i] = ' '; i++; } i--; continue; }
            if (c == '/' && i + 1 < output.Length && output[i + 1] == '*') { output[i++] = ' '; output[i] = ' '; while (i + 1 < output.Length && !(output[i] == '*' && output[i + 1] == '/')) { if (output[i] != '\n' && output[i] != '\r') output[i] = ' '; i++; } if (i + 1 < output.Length) { output[i++] = ' '; output[i] = ' '; } continue; }
            if (c != '\n' && c != '\r' && !char.IsWhiteSpace(c)) output[i] = ' ';
        }
        return new string(output);
    }

    private static string RenderSequence(AnimationSequenceUse sequence) =>
        $"SeqTrack({Quote(sequence.Name)}, {Quote(sequence.Anm2Name)}, {HeaderNumber(sequence.SourceStartFrame, sequence.SourceStartFrameExpression)}, {HeaderNumber(sequence.SourceEndFrame, sequence.SourceEndFrameExpression)}, {HeaderNumber(sequence.FPS, sequence.FpsExpression)}, {HeaderInteger(sequence.WeightMode, sequence.WeightModeExpression)}, {HeaderNumber(sequence.WeightTime, sequence.WeightTimeExpression)})" +
        (sequence.Events.IsDefaultOrEmpty ? string.Empty : "\n{\n\t" + string.Join("\n\t", sequence.Events.Select(RenderEvent)) + "\n}");

    private static string RenderEvent(AnimationEvent e)
    {
        string frame = e.FrameExpression ?? Format(e.LocalFrame);
        string id = PreservedIdExpression(e);
        string slot = e.RequiredSlot?.ToString(CultureInfo.InvariantCulture) ?? e.SlotExpression;
        string call = $"{(e.Delivery == AnimationEventDelivery.MustSendEvent ? "MustSendEvent" : "Event")}({frame}, {id}, {slot})";
        return e.Actions.IsDefaultOrEmpty ? call : call + "\n{\n\t" + string.Join("\n\t", e.Actions.Select(RenderAction)) + "\n}";
    }

    private static string RenderAction(AnimationEventAction action) =>
        action.Keyword + "(" + string.Join(", ", action.Arguments.Select(a => string.IsNullOrEmpty(a.Text) && a.Value is not null ? Quote(a.Value) : a.Text)) + ")";

    private static string PreservedIdExpression(AnimationEvent animationEvent)
    {
        string expression = animationEvent.IdExpression.Trim();
        if (animationEvent.EventId is not int id) return expression;
        bool matches = int.TryParse(expression, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericId)
            ? numericId == id
            : Dl1AnimationEventCatalog.FindEvent(expression)?.Id == id;
        return matches ? expression : id.ToString(CultureInfo.InvariantCulture);
    }

    private static string DetectIndent(string source, int index)
    {
        int line = index;
        while (line > 0 && source[line - 1] != '\n') line--;
        int cursor = line;
        while (cursor < source.Length && (source[cursor] == ' ' || source[cursor] == '\t')) cursor++;
        return source[line..cursor];
    }

    private readonly record struct Span(int Start, int Length) { public int End => Start + Length; }
    private sealed record Call(string Name, int Start, int End, Span Arguments);
    private sealed record Edit(Span Span, string Text);
}

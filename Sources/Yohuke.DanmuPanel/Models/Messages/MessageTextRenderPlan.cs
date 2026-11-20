using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Workspace;

namespace Yohuke.DanmuPanel.Models.Messages;

internal sealed class MessageTextRenderPlan
{
    private readonly IReadOnlyList<DanmuEmoticon>? sourceEmoticons;
    private readonly IReadOnlyList<PinyinAnnotation>? sourceAnnotations;

    internal string Content { get; }
    internal IReadOnlyList<MessageTextRun> Runs { get; }
    internal bool HasAnnotations { get; }
    internal IReadOnlyList<string> ImagePaths { get; }

    private MessageTextRenderPlan(string content, IReadOnlyList<DanmuEmoticon>? emoticons,
        IReadOnlyList<PinyinAnnotation>? annotations, IReadOnlyList<MessageTextRun> runs,
        bool hasAnnotations, IReadOnlyList<string> imagePaths)
    {
        Content = content;
        sourceEmoticons = emoticons;
        sourceAnnotations = annotations;
        Runs = runs;
        HasAnnotations = hasAnnotations;
        ImagePaths = imagePaths;
    }

    internal static MessageTextRenderPlan Create(string content, IReadOnlyList<DanmuEmoticon>? emoticons,
        IReadOnlyList<PinyinAnnotation>? annotations)
    {
        if (content.Length == 0)
        {
            return new(content, emoticons, annotations, [], false, []);
        }

        var matches = FindFirstMatches(content, emoticons);
        var contiguous = false;
        var hasPinyin = false;
        var hasMatchingAnnotations = annotations is { Count: > 0 } &&
            AnnotationsMatchContent(content, annotations, out contiguous, out hasPinyin);
        if (matches is null && (!hasMatchingAnnotations || contiguous))
        {
            IReadOnlyList<PinyinAnnotation> runAnnotations = hasMatchingAnnotations ? annotations! : [];
            return new(content, emoticons, annotations, [new(0, content, null, runAnnotations)],
                hasMatchingAnnotations && hasPinyin, []);
        }

        var runs = new List<MessageTextRun>();
        IReadOnlyList<string> imagePaths = [];
        if (matches is not null)
        {
            imagePaths = SplitRuns(content, matches, runs);
        }
        else
        {
            runs.Add(new(0, content, null, []));
        }

        var hasAnnotations = hasMatchingAnnotations && AssignAnnotations(runs, annotations!);
        return new(content, emoticons, annotations, runs.AsReadOnly(), hasAnnotations, imagePaths);
    }

    internal bool Matches(string content, IReadOnlyList<DanmuEmoticon>? emoticons,
        IReadOnlyList<PinyinAnnotation>? annotations)
    {
        return string.Equals(Content, content, StringComparison.Ordinal) &&
            MatchesInput(sourceEmoticons, emoticons) && MatchesInput(sourceAnnotations, annotations);
    }

    private static bool MatchesInput<T>(IReadOnlyList<T>? previous, IReadOnlyList<T>? current)
    {
        return ReferenceEquals(previous, current) ||
            (previous is null or { Count: 0 }) && (current is null or { Count: 0 });
    }

    private static PriorityQueue<DanmuEmoticon, (int Start, int NegativeLength, int InputIndex)>? FindFirstMatches(
        string content, IReadOnlyList<DanmuEmoticon>? emoticons)
    {
        if (emoticons is null)
        {
            return null;
        }

        PriorityQueue<DanmuEmoticon, (int Start, int NegativeLength, int InputIndex)>? matches = null;
        for (var index = 0; index < emoticons.Count; index++)
        {
            var emoticon = emoticons[index];
            if (string.IsNullOrEmpty(emoticon.Text) || string.IsNullOrWhiteSpace(emoticon.ImagePath))
            {
                continue;
            }
            var start = content.IndexOf(emoticon.Text, StringComparison.Ordinal);
            if (start >= 0)
            {
                (matches ??= new()).Enqueue(emoticon, (start, -emoticon.Text.Length, index));
            }
        }
        return matches;
    }

    private static IReadOnlyList<string> SplitRuns(string content,
        PriorityQueue<DanmuEmoticon, (int Start, int NegativeLength, int InputIndex)> matches, List<MessageTextRun> runs)
    {
        var paths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        while (matches.TryDequeue(out var emoticon, out var match))
        {
            if (match.Start >= position)
            {
                if (match.Start > position)
                {
                    runs.Add(new(position, content[position..match.Start], null, []));
                }
                runs.Add(new(match.Start, emoticon.Text, emoticon, []));
                if (seenPaths.Add(emoticon.ImagePath))
                {
                    paths.Add(emoticon.ImagePath);
                }
                position = match.Start + emoticon.Text.Length;
            }

            var next = content.IndexOf(emoticon.Text, position, StringComparison.Ordinal);
            if (next >= 0)
            {
                matches.Enqueue(emoticon, (next, match.NegativeLength, match.InputIndex));
            }
        }
        if (position < content.Length)
        {
            runs.Add(new(position, content[position..], null, []));
        }
        return paths.AsReadOnly();
    }

    private static bool AnnotationsMatchContent(string content, IReadOnlyList<PinyinAnnotation> annotations,
        out bool contiguous, out bool hasPinyin)
    {
        contiguous = true;
        hasPinyin = false;
        var position = 0;
        foreach (var annotation in annotations)
        {
            if (!content.AsSpan(position).StartsWith(annotation.Text, StringComparison.Ordinal))
            {
                return false;
            }
            contiguous &= annotation.SourceStart == position && annotation.SourceLength > 0;
            hasPinyin |= annotation.HasPinyin;
            position += annotation.SourceLength;
        }
        return position == content.Length;
    }

    private static bool AssignAnnotations(List<MessageTextRun> runs, IReadOnlyList<PinyinAnnotation> annotations)
    {
        var fragments = new List<PinyinAnnotation>?[runs.Count];
        var firstRun = 0;
        var previousStart = int.MinValue;
        var hasAnnotations = false;
        foreach (var annotation in annotations)
        {
            if (annotation.SourceLength == 0)
            {
                continue;
            }
            var annotationStart = annotation.SourceStart;
            var annotationEnd = (long)annotationStart + annotation.SourceLength;
            if (annotationStart < previousStart)
            {
                firstRun = FindFirstRunAfter(runs, annotationStart);
            }
            else
            {
                while (firstRun < runs.Count && runs[firstRun].SourceEnd <= annotationStart)
                {
                    firstRun++;
                }
            }
            previousStart = annotationStart;

            for (var index = firstRun; index < runs.Count && runs[index].SourceStart < annotationEnd; index++)
            {
                var run = runs[index];
                if (run.Emoticon is not null)
                {
                    continue;
                }
                var start = Math.Max(run.SourceStart, annotationStart);
                var end = (int)Math.Min(run.SourceEnd, annotationEnd);
                var fragment = start == annotationStart && end == annotationEnd
                    ? annotation
                    : new PinyinAnnotation(start, annotation.Text.Substring(start - annotationStart, end - start), "");
                (fragments[index] ??= []).Add(fragment);
                hasAnnotations |= fragment.HasPinyin;
            }
        }

        for (var index = 0; index < runs.Count; index++)
        {
            if (fragments[index] is { } runAnnotations)
            {
                runs[index] = runs[index] with { Annotations = runAnnotations.AsReadOnly() };
            }
        }
        return hasAnnotations;
    }

    private static int FindFirstRunAfter(IReadOnlyList<MessageTextRun> runs, int position)
    {
        var first = 0;
        var end = runs.Count;
        while (first < end)
        {
            var middle = first + (end - first) / 2;
            if (runs[middle].SourceEnd <= position)
            {
                first = middle + 1;
            }
            else
            {
                end = middle;
            }
        }
        return first;
    }
}

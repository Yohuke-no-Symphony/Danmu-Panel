using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Workspace;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证内联绘制计划的表情匹配、来源位置和注音边界。</summary>
public sealed class MessageTextRenderPlanTests
{
    /// <summary>先取最早起点，同起点取最长，图片只包含实际胜出的表情且按路径去重。</summary>
    [Fact]
    public void EmoticonsMatchTheEarliestStartAndLongestTokenAtThatStart()
    {
        const string CONTENT = "XABCDEF ABC";
        DanmuEmoticon[] emoticons =
        [
            new() { Text = "AB", ImagePath = "shorter" },
            new() { Text = "BCDEF", ImagePath = "overlapped" },
            new() { Text = "ABC", ImagePath = "winner" },
            new() { Text = "ABC", ImagePath = "tie" },
            new() { Text = "EF", ImagePath = "tail" },
            new() { Text = "XABC", ImagePath = " " },
            new() { Text = "", ImagePath = "empty" },
            new() { Text = "none", ImagePath = "absent" }
        ];

        var plan = MessageTextRenderPlan.Create(CONTENT, emoticons, null);

        Assert.Equal(CONTENT, plan.Content);
        Assert.False(plan.HasAnnotations);
        Assert.Collection(plan.Runs,
            run => AssertRun(run, 0, "X"),
            run => AssertRun(run, 1, "ABC", "winner"),
            run => AssertRun(run, 4, "D"),
            run => AssertRun(run, 5, "EF", "tail"),
            run => AssertRun(run, 7, " "),
            run => AssertRun(run, 8, "ABC", "winner"));
        Assert.Equal(new[] { "winner", "tail" }, plan.ImagePaths);
        Assert.True(plan.Matches(new string(CONTENT.ToCharArray()), emoticons, []));
        Assert.False(plan.Matches(CONTENT, emoticons.ToArray(), null));
        Assert.False(plan.Matches(CONTENT + " ", emoticons, null));
    }

    /// <summary>表情覆盖或切断的注音不外溢，完整 emoji 簇和后续的 UTF-16 位置保留。</summary>
    [Fact]
    public void EmoticonCrossingAnnotationsPreservesUnicodeAndDropsOnlyOverlappingPronunciations()
    {
        const string GRAPHEME = "👩‍💻";
        const string TOKEN = "[笑]";
        const string CONTENT = "好" + GRAPHEME + TOKEN + "尾你";
        var tokenStart = 1 + GRAPHEME.Length;
        var tokenEnd = tokenStart + TOKEN.Length;
        var initial = new PinyinAnnotation(0, "好", "hǎo");
        var final = new PinyinAnnotation(tokenEnd + 1, "你", "nǐ");
        PinyinAnnotation[] annotations =
        [
            initial,
            new(1, GRAPHEME + "[", "clipped-start"),
            new(tokenStart + 1, "笑", "xiào"),
            new(tokenEnd - 1, "]尾", "clipped-end"),
            final
        ];
        DanmuEmoticon[] emoticons = [new() { Text = TOKEN, ImagePath = "image" }];

        var plan = MessageTextRenderPlan.Create(CONTENT, emoticons, annotations);

        Assert.True(plan.HasAnnotations);
        Assert.Collection(plan.Runs,
            run =>
            {
                AssertRun(run, 0, "好" + GRAPHEME);
                Assert.Equal(new[] { initial, new PinyinAnnotation(1, GRAPHEME, "") }, run.Annotations);
            },
            run =>
            {
                AssertRun(run, tokenStart, TOKEN, "image");
                Assert.Empty(run.Annotations);
            },
            run =>
            {
                AssertRun(run, tokenEnd, "尾你");
                Assert.Equal(new[] { new PinyinAnnotation(tokenEnd, "尾", ""), final }, run.Annotations);
            });
        Assert.True(plan.Matches(CONTENT, emoticons, annotations));
        Assert.False(plan.Matches(CONTENT, emoticons, []));
    }

    private static void AssertRun(MessageTextRun run, int offset, string text, string? imagePath = null)
    {
        Assert.Equal(offset, run.SourceStart);
        Assert.Equal(text, run.Text);
        Assert.Equal(imagePath, run.Emoticon?.ImagePath);
    }
}

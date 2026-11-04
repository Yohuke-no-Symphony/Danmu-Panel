using Xunit;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

public sealed class TranslationRequestFactoryTests
{
    [Fact]
    public void SameEffectiveTextUsesSameKeyAndDisplayChangesDoNotInvalidate()
    {
        var settings = new TranslationSettings { Model = "model" };
        var first = TranslationRequestFactory.Create(settings, "你好🙂")!;
        settings.DisplayMode = TranslationDisplayMode.TranslationOnly;
        settings.BacklogMode = TranslationBacklogMode.AllMessages;
        var second = TranslationRequestFactory.Create(settings, "你好")!;
        Assert.Equal(first.CacheKey, second.CacheKey);
    }

    [Fact]
    public void TargetModelPromptAndGlossaryChangesInvalidate()
    {
        var settings = new TranslationSettings { Model = "model" };
        var fingerprints = new HashSet<string> { TranslationRequestFactory.GetConfigurationFingerprint(settings) };
        settings.TargetLanguage = "ja-JP";
        fingerprints.Add(TranslationRequestFactory.GetConfigurationFingerprint(settings));
        settings.Model = "another";
        fingerprints.Add(TranslationRequestFactory.GetConfigurationFingerprint(settings));
        settings.ExtraSystemPrompt = "Use polite language.";
        fingerprints.Add(TranslationRequestFactory.GetConfigurationFingerprint(settings));
        settings.Glossary.Add(new() { Source = "主播", Target = "配信者", TargetLanguage = "ja-JP" });
        fingerprints.Add(TranslationRequestFactory.GetConfigurationFingerprint(settings));
        Assert.Equal(5, fingerprints.Count);
    }

    [Fact]
    public void PromptIncludesOnlyMatchingTermsForSelectedLanguage()
    {
        var settings = new TranslationSettings
        {
            TargetLanguage = "en",
            ExtraSystemPrompt = "Be concise.",
            Glossary =
            [
                new() { Source = "主播", Target = "streamer", TargetLanguage = "en" },
                new() { Source = "礼物", Target = "gift", TargetLanguage = "en" },
                new() { Source = "主播", Target = "配信者", TargetLanguage = "ja" }
            ]
        };
        var request = TranslationRequestFactory.Create(settings, "主播你好")!;
        Assert.Contains("streamer", request.SystemPrompt);
        Assert.Contains("Be concise.", request.SystemPrompt);
        Assert.DoesNotContain("gift", request.SystemPrompt);
        Assert.DoesNotContain("配信者", request.SystemPrompt);
        Assert.Equal("主播你好", request.Text);
    }

    [Fact]
    public void SnapshotDoesNotObserveLaterEdits()
    {
        var settings = new TranslationSettings { Glossary = [new() { Source = "a", Target = "b" }] };
        var snapshot = TranslationRequestFactory.Snapshot(settings);
        settings.Glossary[0].Target = "changed";
        Assert.Equal("b", snapshot.Glossary[0].Target);
    }
}

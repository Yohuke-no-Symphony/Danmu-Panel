using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证发布目录中的中英日语言包具有一致键集和格式参数。</summary>
public sealed class LanguagePackCompletenessTests
{
    /// <summary>三种语言均能独立覆盖相同的界面文案，并保留全部格式参数。</summary>
    [Fact]
    public void EveryLanguageContainsTheSameKeysAndPlaceholders()
    {
        var english = Read("en-US");
        foreach (var language in new[] { "zh-CN", "ja-JP" })
        {
            var translated = Read(language);
            Assert.Equal(english.Keys.Order(), translated.Keys.Order());
            foreach (var (key, value) in english)
            {
                Assert.False(string.IsNullOrWhiteSpace(translated[key]), $"{language}: {key}");
                Assert.Equal(Placeholders(value), Placeholders(translated[key]));
            }
        }
        Assert.Equal("設定…", Read("ja-JP")["Shell.Settings"]);
    }

    private static Dictionary<string, string> Read(string language)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "i18n", language + ".json")));
        Assert.Equal(language, document.RootElement.GetProperty("LanguageID").GetString());
        return document.RootElement.GetProperty("Strings").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!);
    }

    private static string[] Placeholders(string text)
    {
        return Regex.Matches(text, @"(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})")
            .Select(match => match.Groups[1].Value).Order().ToArray();
    }
}

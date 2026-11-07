using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证独立屏蔽设置页、逐条编辑控件和规则列表布局。</summary>
public sealed class DanmuRegexEditorTests
{
    /// <summary>最小窗口宽度下三种语言表头完整显示，切换页面保留逐条规则草稿。</summary>
    [AvaloniaTheory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public async Task BlockingPagePreservesDraftAndFitsMinimumWindowWidth(string language)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var previousLanguage = Localization.SelectedLanguageID;
        Localization.SetLanguage(language);
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Width = 860, Height = 760, Content = view };
        try
        {
            window.Show();
            Select(view, "Reading", window);
            Assert.Empty(view.GetVisualDescendants().OfType<DanmuRegexEditor>());
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Control>(), control =>
                control.Name is "DanmuRegexFilterEnabledCheckBox" or "DanmuRegexPatternsInput");
            Select(view, "Blocking", window);
            var editor = Assert.Single(view.GetVisualDescendants().OfType<DanmuRegexEditor>());
            Assert.Same(view.DanmuRegexEditor, editor);
            var first = editor.Model.Rules[0];
            var second = editor.Model.Rules[1];
            var pattern = FindRow<TextBox>(editor, first, "DanmuRegexPatternInput");
            Assert.False(pattern.AcceptsReturn);
            var display = FindRow<CheckBox>(editor, first, "DanmuRegexBlockDisplayCheckBox");
            var translation = FindRow<CheckBox>(editor, first, "DanmuRegexBlockTranslationCheckBox");
            Assert.True(display.IsEffectivelyEnabled);
            Assert.True(translation.IsEffectivelyEnabled);
            pattern.Text = "^ 广告 $";
            translation.IsChecked = true;
            Assert.False(display.IsChecked);
            Assert.True(display.IsEffectivelyEnabled);
            display.IsChecked = true;
            Assert.True(translation.IsChecked);
            Assert.True(translation.IsEffectivelyEnabled);
            FindRow<CheckBox>(editor, second, "DanmuRegexBlockTranslationCheckBox").IsChecked = true;
            Assert.False(second.BlockDisplay);
            display.IsChecked = false;
            Assert.True(translation.IsEffectivelyEnabled);
            Pump(window);

            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(control => control.Name == "DanmuRegexRulesScrollViewer");
            foreach (var name in new[] { "DanmuRegexPatternHeader", "DanmuRegexBlockDisplayHeader", "DanmuRegexBlockTranslationHeader" })
            {
                var header = editor.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == name);
                var position = header.TranslatePoint(default, scroll)!.Value;
                Assert.True(header.IsEffectivelyVisible);
                Assert.True(header.Bounds.Width > 0);
                Assert.InRange(position.X, 0, scroll.Bounds.Width);
                Assert.InRange(position.X + header.Bounds.Width, 0, scroll.Bounds.Width + 1);
                Assert.InRange(header.TextLayout.WidthIncludingTrailingWhitespace, 0, header.Bounds.Width + 1);
            }

            Select(view, "Reading", window);
            Assert.Empty(view.GetVisualDescendants().OfType<DanmuRegexEditor>());
            Select(view, "Blocking", window);

            Assert.Same(editor, Assert.Single(view.GetVisualDescendants().OfType<DanmuRegexEditor>()));
            Assert.Same(first, editor.Model.Rules[0]);
            Assert.Same(second, editor.Model.Rules[1]);
            Assert.Equal("^ 广告 $", FindRow<TextBox>(editor, first, "DanmuRegexPatternInput").Text);
            Assert.False(FindRow<CheckBox>(editor, first, "DanmuRegexBlockDisplayCheckBox").IsChecked);
            Assert.True(FindRow<CheckBox>(editor, first, "DanmuRegexBlockTranslationCheckBox").IsChecked);
            Assert.False(FindRow<CheckBox>(editor, second, "DanmuRegexBlockDisplayCheckBox").IsChecked);
            Assert.True(FindRow<CheckBox>(editor, second, "DanmuRegexBlockTranslationCheckBox").IsChecked);
            Assert.Equal("^ 广告 $", first.Pattern);
            Assert.False(first.BlockDisplay);
            Assert.True(first.BlockTranslation);
            Assert.True(second.BlockTranslation);
            Assert.All(runtime.Configuration.Settings.DanmuRegexFilter.Rules, rule =>
            {
                Assert.False(rule.BlockDisplay);
                Assert.False(rule.BlockTranslation);
            });
        }
        finally
        {
            window.Close();
            Localization.SetLanguage(previousLanguage);
        }
    }

    /// <summary>新增和删除按钮操作实际行，长列表可纵向滚动，删除全部后保存空列表。</summary>
    [AvaloniaFact]
    public async Task RuleButtonsMaintainNumbersAndAllowSavingEmptyList()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Width = 860, Height = 760, Content = view };
        try
        {
            window.Show();
            Select(view, "Blocking", window);
            var editor = view.DanmuRegexEditor;
            var add = editor.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "AddDanmuRegexRuleButton");
            for (var index = 0; index < 24; index++)
            {
                Execute(add);
                var rule = editor.Model.Rules[^1];
                Assert.Equal("", rule.Pattern);
                Assert.False(rule.BlockDisplay);
                Assert.False(rule.BlockTranslation);
                rule.Pattern = "^rule" + index;
            }
            Pump(window);
            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(control => control.Name == "DanmuRegexRulesScrollViewer");
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            Pump(window);
            Assert.True(scroll.Offset.Y > 0);
            var first = editor.Model.Rules[0];
            Execute(FindRow<Button>(editor, first, "RemoveDanmuRegexRuleButton"));
            Pump(window);
            Assert.Equal(Enumerable.Range(1, editor.Model.Rules.Count), editor.Model.Rules.Select(rule => rule.Number));
            Assert.DoesNotContain(first, editor.Model.Rules);
            foreach (var rule in editor.Model.Rules.ToArray())
            {
                Execute(FindRow<Button>(editor, rule, "RemoveDanmuRegexRuleButton"));
            }
            Pump(window);

            Assert.Empty(editor.Model.Rules);
            Assert.DoesNotContain(editor.GetVisualDescendants().OfType<TextBox>(), control => control.Name == "DanmuRegexPatternInput");
            Assert.True(await view.SaveAsync());
            Assert.Empty(runtime.Configuration.Settings.DanmuRegexFilter.Rules);
            Assert.Empty(runtime.ConfigurationStore.Load().Settings.DanmuRegexFilter.Rules);
        }
        finally
        {
            window.Close();
        }
    }

    private static T FindRow<T>(DanmuRegexEditor editor, object row, string name) where T : Control
    {
        return editor.GetVisualDescendants().OfType<T>()
            .Single(control => control.Name == name && ReferenceEquals(control.DataContext, row));
    }

    private static void Execute(Button button)
    {
        Assert.NotNull(button.Command);
        Assert.True(button.Command.CanExecute(button.CommandParameter));
        button.Command.Execute(button.CommandParameter);
    }

    private static void Select(SettingsView view, string section, Window window)
    {
        Execute(view.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "SettingsSection" + section));
        Pump(window);
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }
}

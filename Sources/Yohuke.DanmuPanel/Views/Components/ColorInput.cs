using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>带颜色预览、透明度选择器和可保留无效草稿的 HEX 输入。</summary>
public sealed class ColorInput : UserControl
{
    private readonly TextBox input;
    private readonly ColorPreviewer preview;
    private readonly TextBlock error;
    private ColorView? picker;
    private Color? fallback;
    private string? observedText;
    private bool synchronizing;

    /// <summary>有效颜色发生变化；空值表示恢复主题默认。</summary>
    public event Action<string?>? ValueChanged;

    /// <summary>当前原始输入是否是有效颜色或空值。</summary>
    public bool IsValid => TryRead(out _);

    /// <summary>创建 HEX 输入与按需打开的颜色选择器。</summary>
    public ColorInput()
    {
        input = new()
        {
            Name = "ColorHexInput", MinWidth = 110, MaxLength = 9,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        preview = new() { Width = 26, Height = 26, IsAccentColorsVisible = false };
        var pickerButton = new Button
        {
            Name = "ColorPickerButton", Width = 36, Height = 32, Padding = new Thickness(3), Content = preview
        };
        pickerButton.Bind(ToolTip.TipProperty, Localization.Observe("Colors.PickColor"));
        pickerButton.Bind(AutomationProperties.NameProperty, Localization.Observe("Colors.PickColor"));
        pickerButton.Click += (_, _) => OpenPicker(pickerButton);
        var reset = new Button
        {
            Name = "ResetColorButton",
            Content = new LucideIcon { Kind = LucideIconKind.RotateCcw, Size = 16 },
            Width = 36,
            VerticalAlignment = VerticalAlignment.Center
        };
        reset.Classes.Add("iconButton");
        reset.Bind(ToolTip.TipProperty, Localization.Observe("Colors.ResetColor"));
        reset.Bind(AutomationProperties.NameProperty, Localization.Observe("Colors.ResetColor"));
        reset.Click += (_, _) =>
        {
            input.Text = "";
        };
        var bar = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 6 };
        bar.Children.Add(pickerButton);
        Grid.SetColumn(input, 1);
        bar.Children.Add(input);
        Grid.SetColumn(reset, 2);
        bar.Children.Add(reset);
        error = new() { Name = "ColorInputError", FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        error.Bind(TextBlock.TextProperty, Localization.Observe("Colors.InvalidColor"));
        error.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppErrorBrush"));
        Content = new StackPanel { Spacing = 4, Children = { bar, error } };
        input.TextChanged += (_, _) => OnInputChanged();
    }

    /// <summary>装载配置值，主动切换方案时替换原始输入，不产生颜色变更通知。</summary>
    public void Load(string? value, Color? defaultColor)
    {
        synchronizing = true;
        try
        {
            fallback = defaultColor;
            input.Text = value is null ? "" : FormatColor(Color.Parse(value));
            observedText = input.Text;
            RefreshPreview();
        }
        finally
        {
            synchronizing = false;
        }
    }

    /// <summary>主题变化时更新默认色块，保留尚未完成的输入。</summary>
    public void RefreshDefault(Color? defaultColor)
    {
        fallback = defaultColor;
        RefreshPreview();
    }

    /// <summary>读取校验后的标准颜色；空值表示不覆盖默认。</summary>
    public bool TryRead(out string? value)
    {
        var text = input.Text?.Trim() ?? "";
        value = null;
        if (text.Length == 0)
        {
            return true;
        }
        if (text.Length is not (7 or 9) || text[0] != '#' ||
            text.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF".AsSpan()) || !Color.TryParse(text, out var color))
        {
            return false;
        }
        value = FormatColor(color);
        return true;
    }

    /// <summary>将焦点移至当前颜色输入。</summary>
    public void FocusInput()
    {
        input.BringIntoView();
        input.Focus();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        Localization.LanguageChanged += OnLanguageChanged;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        Localization.LanguageChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(args);
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        RefreshPreview();
    }

    private void OnInputChanged()
    {
        if (synchronizing || input.Text == observedText)
        {
            return;
        }
        observedText = input.Text;
        RefreshPreview();
        if (TryRead(out var value))
        {
            ValueChanged?.Invoke(value);
        }
    }

    private void RefreshPreview()
    {
        var valid = TryRead(out var value);
        error.IsVisible = !valid;
        input.Watermark = fallback is { } defaultColor ? FormatColor(defaultColor) : Localization.Get("Colors.Inherit");
        if (!valid)
        {
            return;
        }
        var color = value is null ? fallback ?? Colors.Transparent : Color.Parse(value);
        preview.HsvColor = color.ToHsv();
        if (picker is not null)
        {
            var wasSynchronizing = synchronizing;
            synchronizing = true;
            picker.Color = color;
            synchronizing = wasSynchronizing;
        }
    }

    private void OpenPicker(Button target)
    {
        if (picker is null)
        {
            picker = new()
            {
                Name = "ColorPicker", IsColorPaletteVisible = false, IsHexInputVisible = false,
                IsAlphaEnabled = true, IsAlphaVisible = true,
                HexInputAlphaPosition = AlphaComponentPosition.Leading
            };
            picker.ColorChanged += (_, args) =>
            {
                if (!synchronizing)
                {
                    input.Text = FormatColor(args.NewColor);
                }
            };
            target.Flyout = new Flyout { Content = picker };
        }
        RefreshPreview();
        target.Flyout!.ShowAt(target);
    }

    private static string FormatColor(Color color)
    {
        return color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}

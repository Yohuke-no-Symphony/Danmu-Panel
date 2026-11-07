using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.ViewModels.Settings;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>带颜色预览、透明度选择器和可保留无效草稿的 HEX 输入。</summary>
public sealed partial class ColorInput : UserControl
{
    private ColorInputViewModel model = new();
    private ColorView? picker;

    public static readonly DirectProperty<ColorInput, ColorInputViewModel> ModelProperty =
        AvaloniaProperty.RegisterDirect<ColorInput, ColorInputViewModel>(nameof(Model), control => control.Model,
            (control, value) => control.Model = value);

    public ColorInputViewModel Model
    {
        get => model;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(model, value))
            {
                return;
            }
            model.ValueChanged -= OnValueChanged;
            SetAndRaise(ModelProperty, ref model, value);
            model.ValueChanged += OnValueChanged;
            if (picker is not null)
            {
                picker.DataContext = model;
            }
        }
    }

    /// <summary>有效颜色发生变化；空值表示恢复主题默认。</summary>
    public event Action<string?>? ValueChanged;

    /// <summary>当前原始输入是否是有效颜色或空值。</summary>
    public bool IsValid => Model.IsValid;

    /// <summary>创建 HEX 输入与按需打开的颜色选择器。</summary>
    public ColorInput()
    {
        InitializeComponent();
        model.ValueChanged += OnValueChanged;
    }

    /// <summary>装载配置值，主动切换方案时替换原始输入，不产生颜色变更通知。</summary>
    public void Load(string? value, Color? defaultColor)
    {
        Model.Load(value, defaultColor);
    }

    /// <summary>主题变化时更新默认色块，保留尚未完成的输入。</summary>
    public void RefreshDefault(Color? defaultColor)
    {
        Model.RefreshDefault(defaultColor);
    }

    /// <summary>读取校验后的标准颜色；空值表示不覆盖默认。</summary>
    public bool TryRead(out string? value)
    {
        return Model.TryRead(out value);
    }

    /// <summary>将焦点移至当前颜色输入。</summary>
    public void FocusInput()
    {
        var input = this.FindControl<TextBox>("ColorHexInput")!;
        input.BringIntoView();
        input.Focus();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        Localization.LanguageChanged += OnLanguageChanged;
        Model.RefreshLocalization();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        Localization.LanguageChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(args);
    }

    private void OnValueChanged(string? value)
    {
        ValueChanged?.Invoke(value);
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        Model.RefreshLocalization();
    }

    private void Picker_OnClick(object? sender, RoutedEventArgs args)
    {
        var target = (Button)sender!;
        if (picker is null)
        {
            picker = (ColorView)((IDataTemplate)Resources["ColorPickerTemplate"]!).Build(Model)!;
            picker.DataContext = Model;
            picker.ColorChanged += (_, change) =>
            {
                if (change.NewColor != Model.PreviewColor)
                {
                    Model.RawText = ColorInputViewModel.FormatColor(change.NewColor);
                }
            };
            target.Flyout = new Flyout { Content = picker };
        }
        target.Flyout!.ShowAt(target);
    }
}

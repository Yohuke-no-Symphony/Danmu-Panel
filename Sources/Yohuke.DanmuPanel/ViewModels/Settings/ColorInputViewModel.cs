using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>保留 HEX 原始草稿，并独立管理验证、继承色和颜色预览。</summary>
public sealed class ColorInputViewModel : ObservableObject
{
    private string rawText = "";
    private Color? defaultColor;
    private Color previewColor;
    private bool loading;

    public string RawText
    {
        get => rawText;
        set
        {
            if (!SetProperty(ref rawText, value ?? ""))
            {
                return;
            }
            RefreshPreview();
            if (!loading && TryRead(out var color))
            {
                ValueChanged?.Invoke(color);
            }
        }
    }

    public bool IsValid => TryRead(out _);
    public bool HasError => !IsValid;
    public string Watermark => defaultColor is { } color ? FormatColor(color) : Localization.Get("Colors.Inherit");
    public Color PreviewColor
    {
        get => previewColor;
        private set
        {
            if (SetProperty(ref previewColor, value))
            {
                OnPropertyChanged(nameof(PreviewHsvColor));
            }
        }
    }

    public HsvColor PreviewHsvColor => PreviewColor.ToHsv();
    public IRelayCommand ResetCommand { get; }

    /// <summary>有效草稿改变时通知宿主；装载和默认色刷新不产生通知。</summary>
    public event Action<string?>? ValueChanged;

    /// <summary>创建不依赖任何输入控件的颜色草稿。</summary>
    public ColorInputViewModel()
    {
        ResetCommand = new RelayCommand(() => RawText = "");
    }

    /// <summary>装载标准配置并替换草稿，避免产生用户编辑事件。</summary>
    public void Load(string? value, Color? fallback)
    {
        loading = true;
        try
        {
            defaultColor = fallback;
            RawText = value is null ? "" : FormatColor(Color.Parse(value));
            RefreshPreview();
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>更新继承色及语言提示，保留原始输入。</summary>
    public void RefreshDefault(Color? fallback)
    {
        defaultColor = fallback;
        RefreshPreview();
    }

    /// <summary>读取有效颜色；空值表示继承主题。</summary>
    public bool TryRead(out string? value)
    {
        var text = RawText.Trim();
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

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Watermark));
    }

    internal static string FormatColor(Color color)
    {
        return color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void RefreshPreview()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(Watermark));
        if (TryRead(out var value))
        {
            PreviewColor = value is null ? defaultColor ?? Colors.Transparent : Color.Parse(value);
        }
    }
}

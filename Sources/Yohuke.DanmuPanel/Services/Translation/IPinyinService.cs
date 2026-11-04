namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>为包含汉字的名称生成本地带声调拼音。</summary>
public interface IPinyinService
{
    string GetPinyin(string text);
}

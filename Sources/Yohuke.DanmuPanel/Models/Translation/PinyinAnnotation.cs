namespace Yohuke.DanmuPanel.Models.Translation;

/// <summary>保留原文位置及对应读音的不可变文本片段。</summary>
public sealed record PinyinAnnotation
{
    public int SourceStart { get; }
    public string Text { get; }
    public string Pinyin { get; }
    public int SourceLength => Text.Length;
    public bool HasPinyin => Pinyin.Length > 0;

    /// <summary>创建按 UTF-16 索引定位的注音片段；无读音时保留原文。</summary>
    public PinyinAnnotation(int sourceStart, string text, string pinyin)
    {
        SourceStart = sourceStart;
        Text = text;
        Pinyin = pinyin;
    }
}

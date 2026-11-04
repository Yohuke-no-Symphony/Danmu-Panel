using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>为名称和消息正文生成本地带声调拼音。</summary>
public interface IPinyinService
{
    /// <summary>为名称生成整句拼音；没有可转换汉字或包含假名时返回空字符串。</summary>
    string GetPinyin(string text);

    /// <summary>按整句上下文为正文生成保留原文范围的逐字注音；无可转换汉字时返回空集合。</summary>
    IReadOnlyList<PinyinAnnotation> GetAnnotations(string text);
}

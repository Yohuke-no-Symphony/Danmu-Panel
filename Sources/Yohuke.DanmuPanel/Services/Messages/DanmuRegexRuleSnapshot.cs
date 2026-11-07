using System.Text.RegularExpressions;

namespace Yohuke.DanmuPanel.Services.Messages;

internal sealed class DanmuRegexRuleSnapshot(Regex pattern, bool blockDisplay, bool blockTranslation)
{
    internal Regex Pattern { get; } = pattern;
    internal bool BlockDisplay { get; } = blockDisplay;
    internal bool BlockTranslation { get; } = blockTranslation;
    internal bool BlocksTranslation => BlockDisplay || BlockTranslation;
}

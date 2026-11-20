namespace Yohuke.DanmuPanel.Models.State;

internal enum StartupStage
{
    Configuration,
    TranslationCache,
    Messages,
    Accounts,
    Layout,
    Room,
    Cancelling
}

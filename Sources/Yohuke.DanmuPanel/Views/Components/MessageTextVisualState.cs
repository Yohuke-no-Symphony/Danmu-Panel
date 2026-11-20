using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Views.Components;

internal readonly record struct MessageTextVisualState(string Content, MessageTextRenderPlan? Plan, MessageReply? Reply,
    bool ShowReplyAvatar, bool SeparateLine);

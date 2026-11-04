namespace Yohuke.DanmuPanel.Models.Updates;

/// <summary>成功完成检查后，正式发布与本地版本的关系；检查失败由异常表示。</summary>
public enum AppUpdateCheckStatus
{
    NO_RELEASE,
    UP_TO_DATE,
    UPDATE_AVAILABLE
}

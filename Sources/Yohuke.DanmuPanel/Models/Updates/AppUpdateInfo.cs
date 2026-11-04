namespace Yohuke.DanmuPanel.Models.Updates;

/// <summary>正式发布的版本、展示信息和对应的 GitHub 页面。</summary>
/// <param name="Version">发布版本号。</param>
/// <param name="TagName">GitHub 发布标签。</param>
/// <param name="Name">发布名称。</param>
/// <param name="ReleaseNotes">发布说明原文。</param>
/// <param name="ReleaseUri">该发布在本仓库中的 HTTPS 页面。</param>
public sealed record AppUpdateInfo(Version Version, string TagName, string Name, string ReleaseNotes, Uri ReleaseUri);

using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>为直接通过接口构建的 AXAML 模板设置数据上下文。</summary>
public sealed class ContextBoundDataTemplate(IDataTemplate template) : IDataTemplate
{
    /// <inheritdoc/>
    public bool Match(object? data)
    {
        return template.Match(data);
    }

    /// <inheritdoc/>
    public Control? Build(object? data)
    {
        var control = template.Build(data);
        if (control is not null)
        {
            control.DataContext = data;
        }
        return control;
    }
}

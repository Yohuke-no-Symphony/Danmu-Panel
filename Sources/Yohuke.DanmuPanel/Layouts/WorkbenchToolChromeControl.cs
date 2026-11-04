using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Layouts;

internal sealed class WorkbenchToolChromeControl : ToolChromeControl
{
    private Button? closeButton;

    protected override Type StyleKeyOverride => typeof(ToolChromeControl);

    /// <summary>
    /// 在宿主接线前接管面板关闭，并为标题按钮应用紧凑样式。
    /// </summary>
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (closeButton is not null)
        {
            closeButton.Click -= OnCloseButtonClick;
        }

        closeButton = e.NameScope.Find<Button>("PART_CloseButton");

        if (closeButton is not null)
        {
            closeButton.Click += OnCloseButtonClick;
        }

        base.OnApplyTemplate(e);

        if (e.NameScope.Find<TextBlock>("PART_Title") is { Parent: Panel titleHost } title)
        {
            var badge = new ContentControl
            {
                Name = "PART_HeaderBadge",
                VerticalContentAlignment = VerticalAlignment.Center,
                [!ContentProperty] = new Binding(nameof(IDock.ActiveDockable)),
                ContentTemplate = new FuncDataTemplate<WorkbenchDockPanel>((panel, _) => CreateHeaderBadge(panel))
            };
            var titleLayout = new Grid
            {
                ColumnDefinitions = new("*,Auto"),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var titleIndex = titleHost.Children.IndexOf(title);
            titleHost.Children.Remove(title);
            Grid.SetColumn(badge, 1);
            titleLayout.Children.Add(title);
            titleLayout.Children.Add(badge);
            titleHost.Children.Insert(titleIndex, titleLayout);
        }

        if (closeButton?.Parent is StackPanel headerButtons)
        {
            headerButtons.Children.Insert(0, new ContentControl
            {
                Name = "PART_HeaderActions",
                VerticalContentAlignment = VerticalAlignment.Center,
                [!ContentProperty] = new Binding(nameof(IDock.ActiveDockable)),
                ContentTemplate = new FuncDataTemplate<WorkbenchDockPanel>((panel, _) => CreateHeaderActions(panel))
            });
        }

        foreach (var name in new[]
                     { "PART_MenuButton", "PART_PinButton", "PART_MaximizeRestoreButton", "PART_CloseButton" })
        {
            if (e.NameScope.Find<Button>(name) is { } button)
            {
                button.Classes.Add("dockChromeButton");
            }
        }
    }

    private static Control? CreateHeaderActions(WorkbenchDockPanel? panel)
    {
        if (panel?.View is not IWorkbenchHeaderActions { HeaderActionsTemplate: { } template })
        {
            return null;
        }

        var actions = template.Build(panel.View);

        if (actions is not null)
        {
            actions.DataContext = panel.View;
        }

        return actions;
    }

    private static Control? CreateHeaderBadge(WorkbenchDockPanel? panel)
    {
        if (panel?.View is not IWorkbenchHeaderBadge { HeaderBadgeTemplate: { } template })
        {
            return null;
        }

        var badge = template.Build(panel.View);
        if (badge is not null)
        {
            badge.DataContext = panel.View;
        }
        return badge;
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (DataContext is IDock { Factory: { } factory, ActiveDockable: { } dockable })
        {
            factory.CloseDockable(dockable);
        }
    }
}

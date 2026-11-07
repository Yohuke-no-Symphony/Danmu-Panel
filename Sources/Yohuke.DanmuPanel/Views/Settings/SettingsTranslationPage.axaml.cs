using Avalonia.Controls;
using Yohuke.DanmuPanel.ViewModels.Settings;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>承载翻译输入与术语表，仅桥接表格编辑生命周期。</summary>
public sealed partial class SettingsTranslationPage : UserControl
{
    private readonly DataGrid glossaryGrid;

    /// <summary>加载翻译页面及其术语表。</summary>
    public SettingsTranslationPage()
    {
        InitializeComponent();
        glossaryGrid = this.FindControl<DataGrid>("GlossaryGrid")!;
    }

    internal void CommitEdits()
    {
        glossaryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        glossaryGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    internal void ScrollToTerm(GlossaryRow term)
    {
        glossaryGrid.ScrollIntoView(term, glossaryGrid.Columns[0]);
    }

    private void Glossary_OnLoadingRow(object? sender, DataGridRowEventArgs args)
    {
        args.Row.ContextMenu = new GlossaryRowMenu
        {
            DataContext = args.Row,
            RemoveCommand = ((SettingsTranslationViewModel)DataContext!).RemoveGlossaryTermCommand
        };
    }
}

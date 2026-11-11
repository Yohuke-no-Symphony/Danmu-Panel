using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Layouts;

namespace Yohuke.DanmuPanel.Menus;

internal sealed class WorkbenchMenuCatalog : IDisposable
{
    private readonly Dictionary<string, RelayCommand> panelCommands = [];
    private readonly Dictionary<string, RelayCommand> layoutCommands = [];
    private readonly List<IRelayCommand> commands = [];
    private readonly Func<bool> isBlocked;
    private readonly Action<Exception> reportFailure;
    private readonly RelayCommand clearRoom;
    private readonly RelayCommand clearAll;
    private readonly RelayCommand exit;
    private readonly RelayCommand saveLayout;
    private readonly RelayCommand saveAsLayout;
    private readonly RelayCommand restoreDefault;
    private WorkbenchLayoutController? layouts;

    internal WorkbenchMenuCatalog(Action settings, Action about, Action clearRoom, Action clearAll, Action exit,
        Action saveAs, Func<bool> isBlocked, Action<Exception> reportFailure)
    {
        this.isBlocked = isBlocked;
        this.reportFailure = reportFailure;
        SettingsCommand = CreateCommand(settings);
        AboutCommand = CreateCommand(about);
        this.clearRoom = CreateCommand(clearRoom);
        this.clearAll = CreateCommand(clearAll);
        this.exit = CreateCommand(exit);
        saveLayout = CreateCommand(() => layouts!.SaveCurrent(),
            () => layouts is not null && layouts.CurrentPresetId != WorkspaceLayoutPresets.DEFAULT);
        saveAsLayout = CreateCommand(saveAs, () => layouts is not null);
        restoreDefault = CreateCommand(() => layouts!.RestoreDefault(), () => layouts is not null);

        foreach (var id in WorkbenchPanelIds.All)
        {
            panelCommands.Add(id, CreateCommand(() => layouts!.ShowPanel(id), () => layouts is not null));
        }

        Localization.LanguageChanged += OnLanguageChanged;
    }

    internal event Action? Changed;
    internal RelayCommand SettingsCommand { get; }
    internal RelayCommand AboutCommand { get; }

    internal IReadOnlyList<WorkbenchMenuEntry> Entries
    {
        get
        {
            var layoutEntries = new List<WorkbenchMenuEntry>();

            foreach (var preset in layouts?.Presets ?? [])
            {
                if (!layoutCommands.TryGetValue(preset.Id, out var command))
                {
                    var id = preset.Id;
                    command = CreateCommand(() => layouts!.ApplyPreset(id), () => layouts is not null);
                    layoutCommands.Add(id, command);
                }

                layoutEntries.Add(new($"layout:{preset.Id}",
                    WorkspaceLayoutPresets.GetDisplayName(preset.Id, preset.Name),
                    preset.Id == WorkspaceLayoutPresets.DEFAULT ? LucideIconKind.PanelsTopLeft : LucideIconKind.LayoutTemplate,
                    command, preset.Id == layouts!.CurrentPresetId, true));
            }

            if (layoutEntries.Count > 0)
            {
                layoutEntries.Add(new("separator:layout-actions", "", IsSeparator: true));
            }

            layoutEntries.AddRange([
                new("layout-save", Localization.Get("Shell.Layout.Save"), LucideIconKind.Save, saveLayout),
                new("layout-save-as", Localization.Get("Shell.Layout.SaveAs"), LucideIconKind.SaveAll, saveAsLayout),
                new("layout-default", Localization.Get("Shell.Layout.RestoreDefault"), LucideIconKind.RotateCcw, restoreDefault)
            ]);

            return
            [
                new("application", Localization.Get("Shell.Menu.Application"), Children:
                [
                    new("about", Localization.Get("About.Menu"), LucideIconKind.Info, AboutCommand),
                    new("settings", Localization.Get("Shell.Settings"), LucideIconKind.Settings, SettingsCommand,
                        Gesture: new(Key.OemComma, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)),
                    new("separator:application-data", "", IsSeparator: true),
                    new("clear-room", Localization.Get("Shell.ClearRoom"), LucideIconKind.Eraser, clearRoom),
                    new("clear-all", Localization.Get("Shell.ClearAll"), LucideIconKind.Trash2, clearAll),
                    new("separator:application-exit", "", IsSeparator: true),
                    new("exit", Localization.Get("Shell.Exit"), LucideIconKind.LogOut, exit)
                ]),
                new("views", Localization.Get("Shell.Menu.View"), Children: WorkbenchPanelIds.All.Select(id =>
                    new WorkbenchMenuEntry($"panel:{id}", WorkbenchDockPanel.GetLocalizedTitle(id), GetPanelIcon(id),
                        panelCommands[id], layouts?.IsVisible(id) == true, true)).ToArray()),
                new("layouts", Localization.Get("Shell.Menu.Layout"), Children: layoutEntries)
            ];
        }
    }

    internal void AttachLayouts(WorkbenchLayoutController controller)
    {
        layouts = controller;
        layouts.Changed += OnLayoutChanged;
        Refresh();
    }

    internal void Refresh()
    {
        foreach (var command in commands)
        {
            command.NotifyCanExecuteChanged();
        }

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Localization.LanguageChanged -= OnLanguageChanged;

        if (layouts is not null)
        {
            layouts.Changed -= OnLayoutChanged;
        }
    }

    private RelayCommand CreateCommand(Action action, Func<bool>? canExecute = null)
    {
        var command = new RelayCommand(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                reportFailure(exception);
            }
        }, () => !isBlocked() && (canExecute?.Invoke() ?? true));
        commands.Add(command);
        return command;
    }

    private static LucideIconKind GetPanelIcon(string id)
    {
        return id switch
        {
            WorkbenchPanelIds.WORKSPACE => LucideIconKind.FolderTree,
            WorkbenchPanelIds.DANMU => LucideIconKind.MessagesSquare,
            WorkbenchPanelIds.SUPER_CHAT => LucideIconKind.BadgeDollarSign,
            WorkbenchPanelIds.GIFT => LucideIconKind.Gift,
            WorkbenchPanelIds.RED_POCKET => LucideIconKind.Mail,
            WorkbenchPanelIds.GUARD => LucideIconKind.Ship,
            WorkbenchPanelIds.TEST_MESSAGES => LucideIconKind.FlaskConical,
            WorkbenchPanelIds.LOG => LucideIconKind.Logs,
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };
    }

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        Refresh();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Refresh();
    }
}

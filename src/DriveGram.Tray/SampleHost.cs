using DriveGram.Host;
using DriveGram.Tray.Setup;

namespace DriveGram.Tray;

/// <summary>
/// A stand-in for the tray app, so the main window can be opened (and photographed) without Telegram,
/// without a drive and without touching anything the user has: two sample accounts, three sample drives,
/// sample rows in the transfers list. A developer aid only — see Program's "--snapshot-main".
/// Every action does nothing: the windows it opens are for looking at.
/// </summary>
sealed class SampleHost : IMainHost
{
    readonly List<DriveController> _drives = [];
    readonly List<AccountInfo> _accounts =
    [
        new("1", 1, "@ori_storage", 1, ""),
        new("2", 2, "@ori_backup", 1, ""),
    ];

    public SampleHost(SynchronizationContext ui)
    {
        Preparer = new Preparer(ui);
        foreach (var (letter, label, account, connected) in new[]
                 {
                     ("T:", "Photos", "1", true),
                     ("U:", "ארכיון", "1", true),
                     ("V:", "Work", "2", false),
                 })
        {
            var state = AppState.Open("sample-" + letter[0]);
            state.Config.MountPoint = letter;
            state.Config.VolumeLabel = label;
            state.Config.AccountKey = account;
            state.Config.AccountName = _accounts.First(a => a.Key == account).Name;
            state.Config.ApiId = 1;
            state.Config.NetworkDrive = true;
            _drives.Add(new DriveController(state) { Problem = connected ? null : L.T("לא מחובר", "not connected") });
        }
    }

    public IReadOnlyList<DriveController> Drives => _drives;
    public Preparer Preparer { get; }
    public DailyUploads Daily { get; } = new();

    public List<AccountInfo> Accounts() => _accounts;

    public string DriveStatus(DriveController d) => d.Letter switch
    {
        "T:" => L.T("מעלה: 3 קבצים, 11.6 GB, 38%, עוד כ־12 דקות", "uploading: 3 files, 11.6 GB, 38%, about 12 minutes left"),
        "U:" => L.T("הכל שמור בטלגרם ✓", "everything is saved in Telegram ✓"),
        _ => L.T("לא מחובר", "not connected"),
    };

    public void OpenDriveNow(DriveController d) { }
    public Task ConnectOrDisconnectAsync(DriveController d) => Task.CompletedTask;

    public List<ToolStripItem> DriveItems(DriveController d) =>
    [
        new ToolStripMenuItem(L.T("פתח את הכונן", "Open the drive")),
        new ToolStripMenuItem(L.T("סל המחזור…", "Recycle bin…")),
        new ToolStripSeparator(),
        new ToolStripMenuItem(L.T("נתק", "Disconnect")),
    ];

    public Task AddAccountAsync() => Task.CompletedTask;
    public Task AddDrivesAsync(AccountInfo account, bool createNew) => Task.CompletedTask;
    public Task SignOutAccountAsync(AccountInfo a) => Task.CompletedTask;

    public void SetPaused(DriveController d, bool paused) { }
    public void ResumeEverything() { }
    public void ResumeOnly(IReadOnlyList<(DriveController D, string Id)> files) { }
    public void LiftCap() { }
    public void OpenSettings() { }
    public void PrepareFiles(IEnumerable<(DriveSession Session, string Id)> files) { }
    public Task<long> FreeFiles(IEnumerable<(DriveSession Session, string Id)> files, bool confirmed = false) => Task.FromResult(0L);
    public void Notify(string title, string text) { }

    public void SetLanguage(string preference) { }
    public void SetTheme(ThemeKind kind) => Theme.Set(kind);
    public void SetLimits(double alertGb, double capGb) { }
}

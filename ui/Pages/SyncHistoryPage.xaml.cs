using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CloudRedirect.Resources;
using CloudRedirect.Services;
using Wpf.Ui.Controls;

namespace CloudRedirect.Pages;

// Per-app sync status from the DLL's history files (cloud_redirect\history\*.jsonl).
// Read-only and local: works without a cloud sign-in.
public partial class SyncHistoryPage : Page
{
    private const int RecentPerApp = 20;

    private static readonly Brush GoodBrush = Frozen(0x47, 0x9F, 0x42);
    private static readonly Brush BadBrush = Frozen(0xC4, 0x3B, 0x3B);
    private static readonly Brush WarnBrush = Frozen(0xC2, 0x7C, 0x0E);

    private readonly SteamStoreClient _storeClient = SteamStoreClient.Shared;
    private List<AppHistory> _apps = new();
    private bool _loading;

    public SyncHistoryPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var events = await Task.Run(SyncHistoryReader.ReadAll);
            var expanded = _apps.Where(a => a.IsExpanded).Select(a => a.Key).ToHashSet();
            var names = _apps.ToDictionary(a => a.AppId, a => a.Name);

            _apps = events
                .GroupBy(e => (e.AccountId, e.AppId))
                .Select(g => new AppHistory(g.Key.AccountId, g.Key.AppId, g.ToList()))
                .OrderByDescending(a => a.LastTime)
                .ToList();

            bool multiAccount = _apps.Select(a => a.AccountId).Distinct().Count() > 1;
            foreach (var app in _apps)
            {
                app.ShowAccount = multiAccount;
                app.IsExpanded = expanded.Contains(app.Key);
                if (names.TryGetValue(app.AppId, out var n)) app.Name = n;
            }

            AppList.ItemsSource = _apps;
            EmptyText.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            await ResolveNamesAsync();
        }
        catch (Exception ex)
        {
            EmptyText.Text = S.Format("History_LoadFailed", ex.Message);
            EmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task ResolveNamesAsync()
    {
        var ids = _apps.Where(a => string.IsNullOrEmpty(a.Name)).Select(a => a.AppId).Distinct().ToList();
        if (ids.Count == 0) return;
        try
        {
            var infos = await _storeClient.GetAppInfoAsync(ids);
            foreach (var app in _apps)
                if (infos.TryGetValue(app.AppId, out var info) && !string.IsNullOrEmpty(info.Name))
                    app.Name = info.Name;
        }
        catch { /* names are cosmetic; the app id is shown instead */ }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = SyncHistoryReader.GetHistoryDir();
        if (dir == null) return;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { }
    }

    private void ExpandCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AppHistory app })
            app.IsExpanded = !app.IsExpanded;
    }

    internal static string FormatTime(DateTimeOffset t) =>
        t.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    internal static string Describe(SyncHistoryEvent e)
    {
        string text = e.Kind switch
        {
            SyncHistoryReader.Upload => S.Format("History_Ev_Upload", e.Files, e.Deleted, e.ChangeNumber),
            SyncHistoryReader.UploadFailed => S.Get("History_Ev_UploadFailed"),
            SyncHistoryReader.Conflict => S.Get("History_Ev_Conflict"),
            SyncHistoryReader.CloudUnreachable => S.Get("History_Ev_CloudUnreachable"),
            SyncHistoryReader.SaveNotSynced => S.Get("History_Ev_SaveNotSynced"),
            _ => e.Kind,
        };
        return string.IsNullOrEmpty(e.Message) ? text : $"{text}: {e.Message}";
    }

    internal static (SymbolRegular symbol, Brush brush) Look(SyncHistoryEvent e) => e.Kind switch
    {
        SyncHistoryReader.Upload => (SymbolRegular.CloudCheckmark24, GoodBrush),
        SyncHistoryReader.UploadFailed => (SymbolRegular.CloudDismiss24, BadBrush),
        SyncHistoryReader.CloudUnreachable => (SymbolRegular.CloudOff24, WarnBrush),
        SyncHistoryReader.SaveNotSynced => (SymbolRegular.Warning24, WarnBrush),
        SyncHistoryReader.Conflict => (SymbolRegular.Warning24, WarnBrush),
        _ => (SymbolRegular.ErrorCircle24, WarnBrush),
    };

    internal sealed class EventRow
    {
        public EventRow(SyncHistoryEvent e)
        {
            TimeText = FormatTime(e.Time);
            Text = Describe(e);
            var look = Look(e);
            Symbol = look.symbol;
            Brush = look.brush;
        }
        public string TimeText { get; }
        public string Text { get; }
        public SymbolRegular Symbol { get; }
        public Brush Brush { get; }
    }

    internal sealed class AppHistory : INotifyPropertyChanged
    {
        private readonly SyncHistoryEvent _last;
        private readonly SyncHistoryEvent? _lastUpload;

        public AppHistory(uint accountId, uint appId, List<SyncHistoryEvent> events)
        {
            AccountId = accountId;
            AppId = appId;
            _last = events[^1];
            _lastUpload = events.LastOrDefault(e => e.Kind == SyncHistoryReader.Upload);
            Recent = events.AsEnumerable().Reverse().Take(RecentPerApp).Select(e => new EventRow(e)).ToList();
            var look = Look(_last);
            StatusSymbol = look.symbol;
            StatusBrush = look.brush;
        }

        public uint AccountId { get; }
        public uint AppId { get; }
        public (uint, uint) Key => (AccountId, AppId);
        public bool ShowAccount { get; set; }
        public DateTimeOffset LastTime => _last.Time;
        public List<EventRow> Recent { get; }
        public SymbolRegular StatusSymbol { get; }
        public Brush StatusBrush { get; }

        private string _name = "";
        public string Name
        {
            get => _name;
            set { _name = value; Notify(nameof(Name)); Notify(nameof(DisplayName)); }
        }

        public string DisplayName
        {
            get
            {
                var baseName = string.IsNullOrEmpty(_name) ? AppId.ToString() : $"{_name} ({AppId})";
                return ShowAccount ? $"{baseName} · {S.Format("History_Account", AccountId)}" : baseName;
            }
        }

        public string StatusLine => $"{FormatTime(_last.Time)} · {Describe(_last)}";

        public string LastUploadLine => _lastUpload == null
            ? S.Get("History_NoUploadYet")
            : S.Format("History_LastUpload", FormatTime(_lastUpload.Time));

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                Notify(nameof(IsExpanded));
                Notify(nameof(ChevronSymbol));
                Notify(nameof(DetailsVisibility));
            }
        }

        public SymbolRegular ChevronSymbol =>
            _isExpanded ? SymbolRegular.ChevronDown24 : SymbolRegular.ChevronRight24;
        public Visibility DetailsVisibility => _isExpanded ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

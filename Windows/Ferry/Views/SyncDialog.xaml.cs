using CommunityToolkit.Mvvm.ComponentModel;
using Ferry.Core;
using Ferry.Core.Remote;
using Ferry.Models;
using Ferry.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Ferry.Views;

public enum SyncRowKind { New, Changed, Deleted }

/// <summary>比較の結果の 1 行</summary>
public sealed partial class SyncRow : ObservableObject
{
    public required string Path { get; init; }
    public required SyncRowKind Kind { get; init; }
    public long? Size { get; init; }
    public bool IsDirectory { get; init; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;

    public string KindText => Kind switch { SyncRowKind.New => "新規", SyncRowKind.Changed => "変更", _ => "削除" };
    public string SizeText => IsDirectory ? "フォルダ" : Size is { } size ? FileEntry.FormatSize(size) : "";
    public Brush BadgeBrush => new SolidColorBrush(Kind switch
    {
        SyncRowKind.New => ColorHelper.FromArgb(255, 0x2E, 0x9E, 0x5B),
        SyncRowKind.Changed => ColorHelper.FromArgb(255, 0x2A, 0x6F, 0xE0),
        _ => ColorHelper.FromArgb(255, 0xD1, 0x43, 0x43),
    });
}

/// <summary>
/// 同期。左右で開いているフォルダを比べて、サイズが違うか送信元の方が新しい（2 秒より）ファイルを転送する。
/// ミラーでは送信先にしかないものを削除する。
/// </summary>
public sealed partial class SyncDialog : ContentDialog
{
    private readonly SessionViewModel _session;
    private readonly string _localRoot;
    private readonly string _remoteRoot;
    private TransferDirection _direction = TransferDirection.Upload;
    private SyncPlanner.Plan? _plan;
    private List<SyncRow> _rows = [];
    private SyncRowKind? _filter;
    private CancellationTokenSource? _scan;
    /// <summary>比較で調べたサーバー側の項目（キーは相対パス）</summary>
    private Dictionary<string, SyncPlanner.Entry> _remoteEntries = [];

    public SyncDialog(XamlRoot root, SessionViewModel session)
    {
        InitializeComponent();
        XamlRoot = root;
        _session = session;
        _localRoot = session.Local.Path;
        _remoteRoot = session.Remote.Path;
        ExcludeText.Text = string.Join("  ", AppServices.Store.ExcludeRules.Patterns);
        Closed += (_, _) => _scan?.Cancel();
        UpdateEndpoints();
    }

    private void UpdateEndpoints()
    {
        var upload = _direction == TransferDirection.Upload;
        (SourceIcon.Glyph, SourcePlace.Text, SourcePath.Text) = upload ? ("", "この PC", _localRoot) : ("", _session.Title, _remoteRoot);
        (DestinationIcon.Glyph, DestinationPlace.Text, DestinationPath.Text) = upload ? ("", _session.Title, _remoteRoot) : ("", "この PC", _localRoot);
        ToolTipService.SetToolTip(SourcePath, SourcePath.Text);
        ToolTipService.SetToolTip(DestinationPath, DestinationPath.Text);
    }

    /// <summary>方向やミラーを変えたら比較し直してもらう</summary>
    private void ResetPlan()
    {
        _scan?.Cancel();
        _plan = null;
        PrimaryButtonText = "比較";
        SecondaryButtonText = "";
        IsPrimaryButtonEnabled = true;
        Show(HintView);
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        _direction = _direction == TransferDirection.Upload ? TransferDirection.Download : TransferDirection.Upload;
        UpdateEndpoints();
        ResetPlan();
    }

    private void Mirror_Toggled(object sender, RoutedEventArgs e) => ResetPlan();

    private void Show(UIElement view)
    {
        foreach (var candidate in new UIElement[] { HintView, ScanningView, FailedView, SyncedView, ReviewView })
            candidate.Visibility = candidate == view ? Visibility.Visible : Visibility.Collapsed;
        ScanningRing.IsActive = view == ScanningView;
    }

    // MARK: 比較

    private async void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_plan is null || _plan.IsEmpty)
        {
            // 「比較」ではダイアログを閉じない
            args.Cancel = true;
            await CompareAsync();
            return;
        }
        var deferral = args.GetDeferral();
        try
        {
            await ExecuteAsync(_plan);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void Secondary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        await CompareAsync();
    }

    private async Task CompareAsync()
    {
        if (_session.Remote.Client is not { } client) return;
        _scan?.Cancel();
        var scan = _scan = new CancellationTokenSource();
        IsPrimaryButtonEnabled = false;
        Show(ScanningView);
        var folders = 0;
        ScannedText.Text = "";
        try
        {
            var rules = AppServices.Store.ExcludeRules;
            var localTask = Task.Run(() => SyncPlanner.ScanLocal(_localRoot, rules), scan.Token);
            var remote = await SyncPlanner.ScanRemoteAsync(_remoteRoot, client, rules,
                _ => ScannedText.Text = $"{++folders} フォルダ", scan.Token);
            var local = await localTask;
            if (scan.IsCancellationRequested) return;
            _remoteEntries = remote;
            var mirror = MirrorSwitch.IsOn;
            _plan = _direction == TransferDirection.Upload
                ? SyncPlanner.Make(local, remote, mirror)
                : SyncPlanner.Make(remote, local, mirror);
            ShowPlan(_plan, folders);
        }
        catch (Exception e) when (!scan.IsCancellationRequested)
        {
            FailedText.Text = BrowserViewModel.Describe(e);
            Show(FailedView);
            IsPrimaryButtonEnabled = true;
        }
        catch (OperationCanceledException) { }
    }

    private void ShowPlan(SyncPlanner.Plan plan, int folders)
    {
        if (plan.IsEmpty)
        {
            // 「比較」と「比較し直す」が並ばないように
            SecondaryButtonText = "";
            SyncedText.Text = $"転送するファイルはありません（サーバーの {folders} フォルダを調べました）";
            Show(SyncedView);
            PrimaryButtonText = "比較";
            IsPrimaryButtonEnabled = true;
            return;
        }
        _rows =
        [
            .. plan.Changes.Select(c => new SyncRow
            {
                Path = c.RelativePath, Size = c.Size,
                Kind = c.Reason == SyncPlanner.ChangeReason.New ? SyncRowKind.New : SyncRowKind.Changed,
            }),
            .. plan.Deletions.Select(d => new SyncRow { Path = d.RelativePath, Kind = SyncRowKind.Deleted, IsDirectory = d.IsDirectory }),
        ];
        _filter = null;
        NewTile.Content = Tile(_rows.Count(r => r.Kind == SyncRowKind.New), "新規");
        ChangedTile.Content = Tile(_rows.Count(r => r.Kind == SyncRowKind.Changed), "変更");
        DeletedTile.Content = Tile(_rows.Count(r => r.Kind == SyncRowKind.Deleted), "削除");
        DeletedTile.Visibility = MirrorSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButtonText = "同期を実行";
        SecondaryButtonText = "比較し直す";
        ApplyFilter();
        Show(ReviewView);
    }

    private static StackPanel Tile(int count, string label)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = count.ToString("N0"), HorizontalAlignment = HorizontalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 });
        return panel;
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        var kind = Enum.Parse<SyncRowKind>((string)((FrameworkElement)sender).Tag);
        _filter = _filter == kind ? null : kind;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        foreach (var (tile, kind) in new[] { (NewTile, SyncRowKind.New), (ChangedTile, SyncRowKind.Changed), (DeletedTile, SyncRowKind.Deleted) })
            tile.IsChecked = _filter == kind;
        RowsList.ItemsSource = _filter is { } filter ? _rows.Where(r => r.Kind == filter).ToList() : _rows;
        UpdateChecked();
    }

    private void UpdateChecked()
    {
        var checkedRows = _rows.Where(r => r.IsChecked).ToList();
        BytesText.Text = FileEntry.FormatSize(checkedRows.Where(r => r.Kind != SyncRowKind.Deleted).Sum(r => r.Size ?? 0));
        CheckedText.Text = $"{checkedRows.Count} 件を実行します";
        IsPrimaryButtonEnabled = checkedRows.Count > 0;
    }

    private void RowCheck_Click(object sender, RoutedEventArgs e) => UpdateChecked();

    private void CheckAll_Click(object sender, RoutedEventArgs e) => SetAllChecked(true);
    private void UncheckAll_Click(object sender, RoutedEventArgs e) => SetAllChecked(false);

    /// <summary>絞り込み中は、見えている項目だけを変える</summary>
    private void SetAllChecked(bool value)
    {
        foreach (var row in _rows.Where(r => _filter is null || r.Kind == _filter)) row.IsChecked = value;
        UpdateChecked();
    }

    // MARK: 実行

    private async Task ExecuteAsync(SyncPlanner.Plan plan)
    {
        var unchecked_ = _rows.Where(r => !r.IsChecked).Select(r => (r.Path, r.Kind == SyncRowKind.Deleted)).ToHashSet();
        var changes = plan.Changes.Where(c => !unchecked_.Contains((c.RelativePath, false))).ToList();
        var deletions = plan.Deletions.Where(d => !unchecked_.Contains((d.RelativePath, true))).ToList();
        var upload = _direction == TransferDirection.Upload;

        // 削除を先に済ませてから転送を待ち行列に入れる（ローカルはごみ箱へ）
        var deleteFailures = 0;
        foreach (var deletion in deletions)
        {
            try
            {
                if (upload && _session.Remote.Client is { } client)
                {
                    var path = RemotePath.Join(_remoteRoot, deletion.RelativePath);
                    await client.DeleteRecursivelyAsync(new RemoteItem
                    {
                        Name = RemotePath.Name(path), Path = path,
                        Kind = deletion.IsDirectory ? RemoteItem.ItemKind.Directory : RemoteItem.ItemKind.File,
                    });
                }
                else if (!upload)
                {
                    var path = Path.Combine(_localRoot, deletion.RelativePath.Replace('/', '\\'));
                    await Task.Run(() => RecycleBin.Send(path, deletion.IsDirectory));
                }
            }
            catch (Exception e)
            {
                AppServices.Log(e, $"同期の削除 {deletion.RelativePath}");
                deleteFailures++;
            }
        }

        var batch = changes.Select(change => new PlannedTransfer
        {
            Direction = _direction,
            LocalPath = Path.Combine(_localRoot, change.RelativePath.Replace('/', '\\')),
            RemotePath = RemotePath.Join(_remoteRoot, change.RelativePath),
            Size = change.Size,
        }).ToList();
        // ダウンロードでは更新日時をサーバーに揃えるので、比較で調べた送信元の日時を渡しておく
        if (!upload)
        {
            batch = batch.Select(t => t with
            {
                SourceModified = _remoteEntries.TryGetValue(RelativeOf(t.RemotePath), out var entry) ? entry.Modified : null,
            }).ToList();
        }
        var items = _session.Transfers.EnqueuePlanned(batch);
        if (items.Count == 0)
        {
            await _session.Local.RefreshAsync();
            await _session.Remote.RefreshAsync();
        }

        // 画面を閉じた後も転送の完了を待って通知する
        if (AppServices.Settings.NotifyOnComplete)
            _ = NotifyWhenDoneAsync(items, upload, deletions.Count - deleteFailures, deleteFailures);
    }

    private string RelativeOf(string remotePath) => remotePath[(_remoteRoot.TrimEnd('/').Length + 1)..];

    private async Task NotifyWhenDoneAsync(IReadOnlyList<TransferItem> items, bool upload, int deleted, int deleteFailures)
    {
        await Task.WhenAll(items.Select(i => i.Completion));
        var finished = items.Count(i => i.State == TransferState.Finished);
        var failed = items.Count(i => i.State == TransferState.Failed) + deleteFailures;
        var cancelled = items.Count(i => i.State == TransferState.Cancelled);
        var parts = new List<string> { $"{(upload ? "アップロード" : "ダウンロード")} {finished} 件" };
        if (deleted > 0) parts.Add($"削除 {deleted} 件");
        if (failed > 0) parts.Add($"失敗 {failed} 件");
        if (cancelled > 0) parts.Add($"中止 {cancelled} 件");
        var title = failed > 0 ? "同期が終わりました（失敗あり）" : "同期が完了しました";
        Notifier.Show(title, $"{_session.Title}：{string.Join("、", parts)}");
    }
}

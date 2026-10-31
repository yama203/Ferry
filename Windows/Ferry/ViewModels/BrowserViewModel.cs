using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Ferry.Core;
using Ferry.Core.Remote;
using Ferry.Models;

namespace Ferry.ViewModels;

public enum PaneSide { Local, Remote }

public enum SortKey { Name, Size, Date }

/// <summary>パンくずの 1 つ</summary>
public sealed record Crumb(string Name, string Path)
{
    public override string ToString() => Name;
}

/// <summary>片側のペイン（ローカルまたはリモート）の状態</summary>
public sealed partial class BrowserViewModel : ObservableObject
{
    public PaneSide Side { get; }
    public bool IsRemote => Side == PaneSide.Remote;
    /// <summary>リモート側の一覧表示・ファイル操作用（転送は別の接続で行う）</summary>
    public IRemoteClient? Client { get; set; }

    /// <summary>ローカルの "" は「この PC」（ドライブの一覧）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp), nameof(Breadcrumbs), nameof(Title))]
    public partial string Path { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    [ObservableProperty]
    public partial SortKey SortKey { get; set; } = SortKey.Name;

    [ObservableProperty]
    public partial bool Ascending { get; set; } = true;

    /// <summary>
    /// 表示する一覧（フォルダが先、その中で選択中の並び順）。
    /// 選択を変えるたびに並べ替えると重いので、中身や並び順が変わったときだけ作り直す。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial IReadOnlyList<FileEntry> VisibleEntries { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    public partial int HistoryCount { get; private set; }

    private IReadOnlyList<FileEntry> _entries = [];
    private readonly Stack<string> _history = new();

    public BrowserViewModel(PaneSide side, bool showHidden)
    {
        Side = side;
        ShowHidden = showHidden;
    }

    public IReadOnlyList<FileEntry> Entries => _entries;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool CanGoBack => HistoryCount > 0;
    public bool CanGoUp => IsRemote ? Path.Length > 0 && Path != "/" : Path.Length > 0;

    public string Title => IsRemote
        ? (Path == "/" ? "/" : RemotePath.Name(Path))
        : Path.Length == 0 ? "この PC" : LocalName(Path);

    public string StatusText => SelectedCount > 0
        ? $"{VisibleEntries.Count} 項目中 {SelectedCount} 項目を選択"
        : $"{VisibleEntries.Count} 項目";

    public IReadOnlyList<Crumb> Breadcrumbs
    {
        get
        {
            if (IsRemote)
            {
                var crumbs = new List<Crumb> { new("/", "/") };
                var current = "";
                foreach (var part in Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    current += "/" + part;
                    crumbs.Add(new Crumb(part, current));
                }
                return crumbs;
            }
            var local = new List<Crumb> { new("この PC", "") };
            if (Path.Length == 0) return local;
            var root = System.IO.Path.GetPathRoot(Path) ?? Path;
            local.Add(new Crumb(root.TrimEnd('\\'), root));
            var path = root;
            foreach (var part in Path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                path = System.IO.Path.Combine(path, part);
                local.Add(new Crumb(part, path));
            }
            return local;
        }
    }

    partial void OnShowHiddenChanged(bool value) => UpdateVisibleEntries();
    partial void OnSortKeyChanged(SortKey value) => UpdateVisibleEntries();
    partial void OnAscendingChanged(bool value) => UpdateVisibleEntries();

    /// <summary>列の見出しを押したとき。同じ列なら向きを変える。</summary>
    public void SortBy(SortKey key)
    {
        if (SortKey == key)
        {
            Ascending = !Ascending;
        }
        else
        {
            SortKey = key;
            Ascending = true;
        }
    }

    private void UpdateVisibleEntries()
    {
        var shown = ShowHidden ? _entries : _entries.Where(e => !e.IsHidden);
        var folders = shown.Where(e => e.IsDirectory).ToList();
        var files = shown.Where(e => !e.IsDirectory).ToList();
        folders.Sort(Compare);
        files.Sort(Compare);
        VisibleEntries = [.. folders, .. files];
    }

    private int Compare(FileEntry a, FileEntry b)
    {
        var result = SortKey switch
        {
            SortKey.Size => a.SortSize.CompareTo(b.SortSize),
            SortKey.Date => a.SortDate.CompareTo(b.SortDate),
            _ => SyncPlanner.NaturalOrder.Compare(a.Name, b.Name),
        };
        // 同じ値なら名前順
        if (result == 0) return SyncPlanner.NaturalOrder.Compare(a.Name, b.Name);
        return Ascending ? result : -result;
    }

    // MARK: 移動

    public async Task LoadAsync(string newPath, bool recordHistory = true)
    {
        IsLoading = true;
        try
        {
            var loaded = await FetchAsync(newPath);
            if (recordHistory && Path != newPath && (Path.Length > 0 || !IsRemote))
            {
                _history.Push(Path);
                HistoryCount = _history.Count;
            }
            Path = newPath;
            _entries = loaded;
            UpdateVisibleEntries();
            ErrorMessage = null;
        }
        catch (Exception e)
        {
            ErrorMessage = Describe(e);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task RefreshAsync() =>
        IsRemote && Path.Length == 0 ? Task.CompletedTask : LoadAsync(Path, recordHistory: false);

    public async Task GoBackAsync()
    {
        if (!_history.TryPop(out var previous)) return;
        HistoryCount = _history.Count;
        await LoadAsync(previous, recordHistory: false);
    }

    public Task GoUpAsync()
    {
        if (!CanGoUp) return Task.CompletedTask;
        return LoadAsync(IsRemote ? RemotePath.Parent(Path) : LocalParent(Path));
    }

    /// <summary>フォルダは開く。ローカルのファイルは既定のアプリで開く（サーバーのファイルは呼び出し側でダウンロード）</summary>
    public async Task OpenAsync(FileEntry entry)
    {
        if (entry.IsDirectory || entry.IsDrive || (IsRemote && entry.IsSymlink))
        {
            await LoadAsync(entry.Path);
        }
        else if (!IsRemote)
        {
            try
            {
                Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                ErrorMessage = $"「{entry.Name}」を開けませんでした: {e.Message}";
            }
        }
    }

    private async Task<IReadOnlyList<FileEntry>> FetchAsync(string directory)
    {
        if (IsRemote)
        {
            if (Client is null) return [];
            return (await Client.ListAsync(directory)).Select(FileEntry.FromRemote).ToList();
        }
        return await Task.Run<IReadOnlyList<FileEntry>>(() =>
        {
            if (directory.Length == 0)
            {
                return DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new FileEntry
                    {
                        Name = string.IsNullOrEmpty(d.VolumeLabel) ? d.Name.TrimEnd('\\') : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})",
                        Path = d.RootDirectory.FullName,
                        IsDirectory = true,
                        IsDrive = true,
                        Size = d.TotalSize,
                    })
                    .ToList();
            }
            return new DirectoryInfo(directory).EnumerateFileSystemInfos().Select(FileEntry.FromLocal).ToList();
        });
    }

    // MARK: ファイル操作

    public string ChildPath(string name) =>
        IsRemote ? RemotePath.Join(Path, name) : System.IO.Path.Combine(Path, name);

    public Task MakeFolderAsync(string name) => PerformAsync(async () =>
    {
        var target = ChildPath(name);
        if (Client is not null) await Client.MakeDirectoryAsync(target);
        else Directory.CreateDirectory(target);
    });

    public Task RenameAsync(FileEntry entry, string name) => PerformAsync(async () =>
    {
        if (Client is not null)
        {
            await Client.RenameAsync(entry.Path, RemotePath.Join(RemotePath.Parent(entry.Path), name));
        }
        else
        {
            var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(entry.Path)!, name);
            if (entry.IsDirectory) Directory.Move(entry.Path, target);
            else File.Move(entry.Path, target);
        }
    });

    /// <summary>リモートは完全に削除、ローカルはごみ箱に入れる</summary>
    public Task DeleteAsync(IReadOnlyList<FileEntry> targets) => PerformAsync(async () =>
    {
        foreach (var entry in targets)
        {
            if (Client is not null)
            {
                var item = new RemoteItem
                {
                    Name = entry.Name, Path = entry.Path,
                    Kind = entry.IsDirectory ? RemoteItem.ItemKind.Directory : RemoteItem.ItemKind.File,
                };
                await Client.DeleteRecursivelyAsync(item);
            }
            else
            {
                await Task.Run(() => RecycleBin.Send(entry.Path, entry.IsDirectory));
            }
        }
    });

    public Task ChangePermissionsAsync(IReadOnlyList<FileEntry> targets, string mode) => PerformAsync(async () =>
    {
        if (Client is null) return;
        foreach (var entry in targets)
        {
            await Client.ChangePermissionsAsync(entry.Path, mode);
        }
    });

    private async Task PerformAsync(Func<Task> operation)
    {
        string? failure = null;
        try
        {
            await operation();
        }
        catch (Exception e)
        {
            failure = Describe(e);
        }
        await RefreshAsync();
        // 再読み込みに成功するとエラー表示が消えるので、操作のエラーは後から設定する
        if (failure is not null) ErrorMessage = failure;
    }

    public static string Describe(Exception e) => e switch
    {
        FerryException ferry => ferry.Message,
        UnauthorizedAccessException => "アクセスが拒否されました。権限を確認してください。",
        DirectoryNotFoundException => "フォルダが見つかりません。",
        FileNotFoundException => "ファイルが見つかりません。",
        IOException io => io.Message,
        _ => e.Message,
    };

    private static string LocalParent(string path)
    {
        // ドライブのルートの上は「この PC」
        if (System.IO.Path.GetPathRoot(path) == path) return "";
        return Directory.GetParent(path.TrimEnd('\\'))?.FullName ?? "";
    }

    private static string LocalName(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? path.TrimEnd('\\') : name;
    }
}

/// <summary>ごみ箱へ移す（Explorer の削除と同じ）</summary>
internal static class RecycleBin
{
    public static void Send(string path, bool isDirectory)
    {
        const Microsoft.VisualBasic.FileIO.UIOption ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
        const Microsoft.VisualBasic.FileIO.RecycleOption recycle = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
        if (isDirectory) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, ui, recycle);
        else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, ui, recycle);
    }
}

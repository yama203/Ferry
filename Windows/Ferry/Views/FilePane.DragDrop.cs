using System.Text.Json;
using Ferry.Models;
using Ferry.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Ferry.Views;

/// <summary>
/// ドラッグ&amp;ドロップ。
/// ・左右のペインの間（アップロード・ダウンロード）
/// ・エクスプローラーからサーバー側へ（アップロード）、エクスプローラーのフォルダを左のペインへ（そこへ移動）
/// ・サーバー側からエクスプローラーへ（ファイルは落とされたときに読み出す仮想ファイル、フォルダは一時フォルダへダウンロード）
/// </summary>
public sealed partial class FilePane
{
    /// <summary>この PC のペインから運ぶ項目（パスの一覧）</summary>
    private const string LocalFormat = "Ferry.LocalPaths";
    /// <summary>サーバーのペインから運ぶ項目（サーバーのパスの一覧）</summary>
    private const string RemoteFormat = "Ferry.RemotePaths";

    private void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var entries = e.Items.OfType<FileEntry>().Where(i => !i.IsDrive).ToList();
        if (entries.Count == 0 || (ViewModel.IsRemote && Session is not { IsConnected: true }))
        {
            e.Cancel = true;
            return;
        }
        var paths = JsonSerializer.Serialize(entries.Select(i => i.Path).ToList());
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        if (ViewModel.IsRemote)
        {
            e.Data.SetData(RemoteFormat, paths);
            var session = Session!;
            // エクスプローラーが受け取るときにだけ作る（Ferry の左のペインに落とした場合は使わない）
            Task<IReadOnlyList<IStorageItem>>? prepared = null;
            e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    prepared ??= RunOnUiAsync(() => RemoteStorageItemsAsync(session, entries));
                    request.SetData(await prepared);
                }
                catch (Exception error)
                {
                    AppServices.Log(error, "エクスプローラーへのドラッグ");
                }
                finally
                {
                    deferral.Complete();
                }
            });
        }
        else
        {
            e.Data.SetData(LocalFormat, paths);
            // エクスプローラーなど他のアプリへも運べるように
            e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    var items = new List<IStorageItem>();
                    foreach (var entry in entries)
                    {
                        items.Add(entry.IsDirectory
                            ? await StorageFolder.GetFolderFromPathAsync(entry.Path)
                            : await StorageFile.GetFileFromPathAsync(entry.Path));
                    }
                    request.SetData(items);
                }
                catch (Exception error)
                {
                    AppServices.Log(error, "ローカルの項目のドラッグ");
                }
                finally
                {
                    deferral.Complete();
                }
            });
        }
    }

    /// <summary>
    /// サーバーの項目をエクスプローラーへ渡す形にする。ファイルは落とされた後にダウンロードする仮想ファイル、
    /// フォルダは一時フォルダへ中身ごとダウンロードしてから渡す。
    /// </summary>
    private static async Task<IReadOnlyList<IStorageItem>> RemoteStorageItemsAsync(SessionViewModel session, IReadOnlyList<FileEntry> entries)
    {
        var temp = Path.Combine(Path.GetTempPath(), "Ferry", "drag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var items = new List<IStorageItem>();
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                await session.Transfers.DownloadAndWaitAsync([SessionViewModel.ToRemoteItem(entry)], temp);
                items.Add(await StorageFolder.GetFolderFromPathAsync(Path.Combine(temp, Core.TransferPlanner.LocalName(entry.Name))));
            }
            else
            {
                var file = entry;
                items.Add(await StorageFile.CreateStreamedFileAsync(Core.TransferPlanner.LocalName(file.Name),
                    request => _ = StreamRemoteFileAsync(session, file, temp, request), null));
            }
        }
        return items;
    }

    /// <summary>エクスプローラーが仮想ファイルを読みに来たら、ダウンロードして中身を流す</summary>
    private static async Task StreamRemoteFileAsync(SessionViewModel session, FileEntry entry, string temp, StreamedFileDataRequest request)
    {
        try
        {
            await RunOnUiAsync(() => session.Transfers.DownloadAndWaitAsync([SessionViewModel.ToRemoteItem(entry)], temp));
            await using (var input = File.OpenRead(Path.Combine(temp, Core.TransferPlanner.LocalName(entry.Name))))
            await using (var output = request.AsStreamForWrite())
            {
                await input.CopyToAsync(output);
            }
            request.Dispose();
        }
        catch (Exception error)
        {
            AppServices.Log(error, "エクスプローラーへのドラッグ");
            request.FailAndClose(StreamedFileFailureMode.Failed);
        }
    }

    /// <summary>転送の待ち行列は UI スレッドで扱うので、そちらで実行して結果を待つ</summary>
    private static Task<T> RunOnUiAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        AppServices.OnUi(async () =>
        {
            try { completion.SetResult(await action()); }
            catch (Exception e) { completion.SetException(e); }
        });
        return completion.Task;
    }

    // MARK: 受け取る側

    private void List_DragOver(object sender, DragEventArgs e)
    {
        var data = e.DataView;
        if (ViewModel.IsRemote)
        {
            // この PC のペインやエクスプローラーから → アップロード（自分のペインからは受け取らない）
            if (Session is { IsConnected: true } && !data.Contains(RemoteFormat)
                && (data.Contains(LocalFormat) || data.Contains(StandardDataFormats.StorageItems)))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = $"「{ViewModel.Title}」へアップロード";
            }
        }
        else if (data.Contains(RemoteFormat))
        {
            if (ViewModel.Path.Length > 0)
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = $"「{ViewModel.Title}」へダウンロード";
            }
        }
        else if (!data.Contains(LocalFormat) && data.Contains(StandardDataFormats.StorageItems))
        {
            // エクスプローラーのフォルダ → そこへ移動
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = "このフォルダを開く";
        }
    }

    private async void List_Drop(object sender, DragEventArgs e)
    {
        var data = e.DataView;
        var deferral = e.GetDeferral();
        try
        {
            if (ViewModel.IsRemote && Session is { IsConnected: true } session)
            {
                var paths = data.Contains(LocalFormat)
                    ? JsonSerializer.Deserialize<List<string>>((string)await data.GetDataAsync(LocalFormat)) ?? []
                    : (await data.GetStorageItemsAsync()).Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                deferral.Complete();
                await session.UploadAsync(paths);
            }
            else if (!ViewModel.IsRemote && data.Contains(RemoteFormat) && Session is { } remoteSession)
            {
                var paths = JsonSerializer.Deserialize<List<string>>((string)await data.GetDataAsync(RemoteFormat)) ?? [];
                deferral.Complete();
                var entries = remoteSession.Remote.Entries.Where(entry => paths.Contains(entry.Path)).ToList();
                await remoteSession.DownloadAsync(entries);
            }
            else if (!ViewModel.IsRemote && data.Contains(StandardDataFormats.StorageItems))
            {
                var item = (await data.GetStorageItemsAsync()).FirstOrDefault();
                deferral.Complete();
                if (item is null) return;
                await ViewModel.LoadAsync(item is StorageFolder ? item.Path : Path.GetDirectoryName(item.Path)!);
            }
        }
        catch (Exception error)
        {
            AppServices.Log(error, "ドロップ");
            ViewModel.ErrorMessage = "ドロップした項目を受け取れませんでした: " + error.Message;
        }
        finally
        {
            try { deferral.Complete(); } catch (Exception) { }
        }
    }
}

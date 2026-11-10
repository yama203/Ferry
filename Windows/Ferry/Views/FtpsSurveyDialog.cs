using Ferry.Core;
using Ferry.Core.Remote;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ferry.Views;

/// <summary>「FTPS に切り替えられる接続先を調べる」。暗号化なしの FTP の接続先をまとめて調べ、選んだものを FTPS（Explicit）にする</summary>
public static class FtpsSurveyDialog
{
    private const string DialogTitle = "FTPS に切り替えられる接続先を調べる";
    /// <summary>同時に調べる件数（説明文にも出す）</summary>
    private const int Concurrency = 3;

    /// <summary>
    /// 調べて、切り替える接続先を選んでもらう。「選んだ接続先を FTPS に切り替える」が押されたら、その一覧を返す
    /// （切り替えは呼ぶ側が行う。開いている画面の転送を確かめてからにするため）
    /// </summary>
    public static async Task<FtpsSurvey?> ShowAsync(XamlRoot root)
    {
        var survey = FtpsSurvey.FromStore(AppServices.Store);
        if (survey.Entries.Count == 0)
        {
            await Dialogs.ShowErrorAsync(root, DialogTitle, "暗号化なしの FTP の接続先はありません。");
            return null;
        }

        var rows = survey.Entries.Select(entry => new Row(entry)).ToList();
        var list = new StackPanel { Spacing = 4 };
        foreach (var row in rows) list.Children.Add(row.Root);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = ThemeBrush("TextFillColorSecondaryBrush") };
        var sharedNote = new TextBlock
        {
            Text = "会社の共有の接続先を選んでいます。切り替えると、会社の全員の設定が FTPS になります。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("SystemFillColorCautionBrush"),
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel { Spacing = 10, Width = 540 };
        content.Children.Add(new TextBlock
        {
            Text = $"暗号化なしの FTP の接続先が {survey.Entries.Count} 件あります（パスワードもファイルの中身もそのまま流れます）。"
                   + $"「調べる」を押すと、保存されているパスワードで、FTPS（Explicit）でつながるかを、同時に {Concurrency} 件ずつ試します。調べるだけでは設定は変わりません。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380, Padding = new Thickness(0, 0, 16, 0) });
        content.Children.Add(summary);
        content.Children.Add(sharedNote);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = DialogTitle,
            Content = content,
            PrimaryButtonText = "調べる",
            CloseButtonText = "閉じる",
            DefaultButton = ContentDialogButton.Primary,
        };
        // 閉じた後に調べ終わることがあるので Dispose しない
        var cancel = new CancellationTokenSource();

        void Refresh()
        {
            foreach (var row in rows) row.Update();
            sharedNote.Visibility = survey.HasSharedSelected ? Visibility.Visible : Visibility.Collapsed;
            var done = survey.Entries.Where(e => e.State == FtpsSurvey.EntryState.Done).ToList();
            if (survey.IsRunning)
            {
                dialog.PrimaryButtonText = "調べています…";
                dialog.IsPrimaryButtonEnabled = false;
                summary.Text = $"{survey.Entries.Count} 件のうち {done.Count} 件を調べました。";
                return;
            }
            if (!survey.HasChecked) return;
            int CountOf(FtpsCheckOutcome outcome) => done.Count(e => e.Result?.Outcome == outcome);
            summary.Text = $"切り替えられる {CountOf(FtpsCheckOutcome.Switchable)} 件・証明書を検証できない {CountOf(FtpsCheckOutcome.CertificateUnverified)} 件・"
                           + $"対応していない {CountOf(FtpsCheckOutcome.NotSupported)} 件・調べられなかった {CountOf(FtpsCheckOutcome.Failed)} 件";
            dialog.PrimaryButtonText = survey.SelectedCount > 0
                ? $"選んだ接続先を FTPS に切り替える（{survey.SelectedCount} 件）"
                : "選んだ接続先を FTPS に切り替える";
            dialog.IsPrimaryButtonEnabled = survey.SelectedCount > 0;
        }

        foreach (var row in rows) row.SelectionChanged += Refresh;
        survey.EntryChanged += (_, _) => AppServices.OnUi(Refresh);

        async Task RunAsync()
        {
            // パスワードは、保管庫に足すなどの副作用が無い読み方で取る
            var running = survey.RunAsync(
                (server, token) => FtpsCheck.CheckAsync(server, AppServices.Passwords.KnownPassword(server.Id), token),
                concurrency: Concurrency, cancellation: cancel.Token);
            Refresh();
            await running;
            // 対応していないと分かった接続先には、接続中の画面で「暗号化されていません」の印を出さない
            var changed = false;
            foreach (var entry in survey.Entries)
            {
                if (entry.Result is { } result) changed |= AppServices.Settings.RememberFtpsCheck(entry.Server.Id, result);
            }
            if (changed) AppServices.Settings.Save();
            if (!cancel.IsCancellationRequested) Refresh();
        }

        dialog.PrimaryButtonClick += (sender, args) =>
        {
            // 調べ終わった後に押されたら、閉じて切り替える
            if (survey.HasChecked && !survey.IsRunning) return;
            args.Cancel = true;
            _ = RunAsync();
        };
        // 調べている途中で閉じたら中止する
        dialog.Closing += (_, args) =>
        {
            if (args.Result != ContentDialogResult.Primary) cancel.Cancel();
        };

        var choice = await Dialogs.ShowAsync(dialog);
        return choice == ContentDialogResult.Primary && survey.SelectedCount > 0 ? survey : null;
    }

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>一覧の 1 行（選ぶ印・名前・結果・理由）</summary>
    private sealed class Row
    {
        public Grid Root { get; } = new() { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
        public event Action? SelectionChanged;

        private readonly FtpsSurvey.Entry _entry;
        private readonly CheckBox _check = new() { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
        private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        private bool _updating;

        public Row(FtpsSurvey.Entry entry)
        {
            _entry = entry;
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var server = entry.Server;
            var names = new StackPanel();
            names.Children.Add(new TextBlock
            {
                Text = entry.IsShared ? $"{server.Name}（会社の共有）" : server.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            names.Children.Add(new TextBlock
            {
                Text = $"{server.Username}@{server.Host}:{server.EffectivePort}",
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            });
            names.Children.Add(_detail);
            Grid.SetColumn(names, 1);
            Grid.SetColumn(_status, 2);
            Root.Children.Add(_check);
            Root.Children.Add(names);
            Root.Children.Add(_status);

            _check.Checked += (_, _) => Toggle(true);
            _check.Unchecked += (_, _) => Toggle(false);
            Update();
        }

        private void Toggle(bool selected)
        {
            if (_updating) return;
            _entry.IsSelected = selected;
            SelectionChanged?.Invoke();
        }

        public void Update()
        {
            _updating = true;
            _check.IsEnabled = _entry.CanSelect;
            _check.IsChecked = _entry.IsSelected;
            _updating = false;

            var result = _entry.Result;
            (_status.Text, _status.Foreground) = _entry.State switch
            {
                FtpsSurvey.EntryState.NotChecked => ("未確認", ThemeBrush("TextFillColorSecondaryBrush")),
                FtpsSurvey.EntryState.Checking => ("調べています…", ThemeBrush("TextFillColorSecondaryBrush")),
                _ => result?.Outcome switch
                {
                    FtpsCheckOutcome.Switchable => ("切り替えられます", ThemeBrush("SystemFillColorSuccessBrush")),
                    FtpsCheckOutcome.CertificateUnverified => ("証明書を検証できません", ThemeBrush("SystemFillColorCautionBrush")),
                    FtpsCheckOutcome.NotSupported => ("対応していません", ThemeBrush("TextFillColorSecondaryBrush")),
                    _ => ("調べられませんでした", ThemeBrush("SystemFillColorCriticalBrush")),
                },
            };
            _detail.Text = result switch
            {
                { Outcome: FtpsCheckOutcome.CertificateUnverified } unverified =>
                    $"{unverified.Reason}。{unverified.Advice}\n選ぶと「証明書を検証せずに FTPS」にします（盗み見は防げますが、なりすましは防げません。暗号化なしよりは安全です）。",
                { Outcome: FtpsCheckOutcome.NotSupported or FtpsCheckOutcome.Failed } failed => failed.Reason ?? "",
                _ => "",
            };
            _detail.Visibility = _detail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}

using Ferry.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.Views;

/// <summary>
/// パスワードの同期（保管庫）の設定欄。状態に応じて、始める・マスターパスワードを入れる・変える・やめる を出す。
/// 設定画面がダイアログなので、マスターパスワードは別のダイアログではなくこの欄の中で入力する。
/// </summary>
public sealed partial class VaultPanel : StackPanel
{
    private readonly string _title;
    private readonly string _description;
    private readonly Func<PasswordVault?> _vault;
    private readonly Func<IEnumerable<Guid>> _serverIds;
    private readonly string _unavailable;
    /// <summary>マスターパスワードの変更の入力欄を開いている</summary>
    private bool _changing;

    /// <param name="vault">保管庫（使えないときは null）</param>
    /// <param name="serverIds">この保管庫に入れる接続先（同期を始めたときに、この PC のパスワードを入れる）</param>
    /// <param name="unavailable">保管庫が使えないときの説明</param>
    public VaultPanel(string title, string description, Func<PasswordVault?> vault, Func<IEnumerable<Guid>> serverIds,
                      string unavailable)
    {
        _title = title;
        _description = description;
        _vault = vault;
        _serverIds = serverIds;
        _unavailable = unavailable;
        Spacing = 8;
        Render();
    }

    private void Render(string? message = null)
    {
        Children.Clear();
        Children.Add(new TextBlock { Text = _title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        Children.Add(Secondary(_description));
        var vault = _vault();
        if (vault is null)
        {
            Children.Add(Secondary(_unavailable));
            return;
        }

        switch (vault.State)
        {
            case PasswordVault.VaultState.Unlocked:
                Children.Add(Status($"同期しています（{vault.Count} 件）", ""));
                var add = new HyperlinkButton { Content = "保存してあるパスワードを同期に追加", Padding = new Thickness(0) };
                add.Click += (_, _) =>
                {
                    var added = AppServices.Passwords.AddKnownPasswords(vault, _serverIds());
                    Render($"{added} 件を追加しました。");
                };
                Children.Add(add);
                if (_changing)
                {
                    AddChangeMasterPassword(vault);
                    break;
                }
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var change = new Button { Content = "マスターパスワードを変更…" };
                change.Click += (_, _) =>
                {
                    _changing = true;
                    Render();
                };
                buttons.Children.Add(change);
                var forget = new Button { Content = "この PC で同期をやめる" };
                ToolTipService.SetToolTip(forget, "この PC ではマスターパスワードを忘れます。他の PC の同期はそのまま続きます。");
                forget.Click += (_, _) =>
                {
                    // 今のパスワードはこの PC に残す
                    foreach (var id in _serverIds())
                    {
                        if (vault.GetPassword(id) is { } password) AppServices.Passwords.SetLocalPassword(id, password);
                    }
                    vault.Forget();
                    Render();
                };
                buttons.Children.Add(forget);
                buttons.Children.Add(ConfirmButton("すべての PC で同期をやめる…",
                    "保管ファイルを消し、どの PC でも同期しなくなります。この PC の分のパスワードは残します。", "やめる", () =>
                    {
                        foreach (var (id, password) in vault.Disable()) AppServices.Passwords.SetLocalPassword(id, password);
                        Render();
                    }));
                Children.Add(buttons);
                break;

            case PasswordVault.VaultState.Locked:
                _changing = false;
                // 鍵は保存してあるのに開けない = 他の PC でマスターパスワードが変更された
                Children.Add(Status(vault.HasOutdatedKey
                    ? "この PC に保存してあるマスターパスワードでは開けなくなりました。他の PC でマスターパスワードが変更されたかもしれません。新しいマスターパスワードを入力してください。"
                    : "他の PC で同期が設定されています。マスターパスワードを入力すると、この PC でも使えます。", ""));
                var master = new PasswordBox { Header = vault.HasOutdatedKey ? "新しいマスターパスワード" : "マスターパスワード", Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
                var unlock = new Button { Content = "同期する", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                void DoUnlock()
                {
                    try
                    {
                        if (vault.Unlock(master.Password))
                        {
                            AppServices.Passwords.AddKnownPasswords(vault, _serverIds());
                            Render("この PC でも同期を始めました。");
                        }
                        else
                        {
                            Render("マスターパスワードが違います。");
                        }
                    }
                    catch (InvalidDataException e)
                    {
                        Render(e.Message);
                    }
                }
                unlock.Click += (_, _) => DoUnlock();
                master.KeyDown += (_, e) =>
                {
                    if (e.Key != Windows.System.VirtualKey.Enter) return;
                    e.Handled = true;
                    DoUnlock();
                };
                Children.Add(master);
                Children.Add(unlock);
                break;

            default:
                _changing = false;
                Children.Add(Status("同期していません", ""));
                var first = new PasswordBox { Header = "新しいマスターパスワード", Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
                var again = new PasswordBox { Header = "もう一度", Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
                var enable = new Button { Content = "同期を始める", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                enable.Click += (_, _) =>
                {
                    if (first.Password.Length < 8)
                    {
                        Render("マスターパスワードは 8 文字以上にしてください。");
                        return;
                    }
                    if (first.Password != again.Password)
                    {
                        Render("2 つのマスターパスワードが違います。");
                        return;
                    }
                    var initial = AppServices.Passwords.KnownPasswords(_serverIds());
                    vault.Enable(first.Password, initial);
                    Render($"同期を始めました（{initial.Count} 件）。他の PC でも同じマスターパスワードを入力してください。");
                };
                Children.Add(first);
                Children.Add(again);
                Children.Add(enable);
                break;
        }
        if (message is not null)
            Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
    }

    /// <summary>
    /// マスターパスワードの変更の入力欄（設定画面がダイアログなので、別のダイアログではなくこの欄の中に出す）。
    /// 手元の鍵だけでは変えられないよう、今のマスターパスワードも聞く
    /// </summary>
    private void AddChangeMasterPassword(PasswordVault vault)
    {
        Children.Add(new TextBlock
        {
            Text = "マスターパスワードの変更",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, 8, 0, 0),
        });
        // 閉じられない注意書きにする（読み飛ばされないように）
        Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsOpen = true,
            IsClosable = false,
            Title = "変更する前に",
            Message = "変更すると、ほかの PC・ほかの人の Ferry は、新しいマスターパスワードを入力するまで保管庫を開けなくなります。新しいマスターパスワードは、別の方法（口頭など）で伝えてください。\n\n"
                + "以前のマスターパスワードを知っている人は、変更前の保管庫の写し（Dropbox の履歴や手元のコピー）から、そのとき入っていたパスワードを読めます。退職した人が出たときなどは、サーバー側のパスワードも変更してください。",
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        var current = new PasswordBox { Header = "今のマスターパスワード", Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        var first = new PasswordBox { Header = "新しいマスターパスワード", Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        var again = new PasswordBox { Header = "もう一度", Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        var change = new Button { Content = "変更する", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var cancel = new Button { Content = "キャンセル" };
        change.Click += (_, _) =>
        {
            // 始めるときと同じ決まり
            if (first.Password.Length < 8)
            {
                Render("マスターパスワードは 8 文字以上にしてください。");
                return;
            }
            if (first.Password != again.Password)
            {
                Render("2 つのマスターパスワードが違います。");
                return;
            }
            if (first.Password == current.Password)
            {
                Render("新しいマスターパスワードが今と同じです。");
                return;
            }
            try
            {
                if (!vault.ChangeMasterPassword(current.Password, first.Password))
                {
                    Render("今のマスターパスワードが違います。");
                    return;
                }
                _changing = false;
                Render("マスターパスワードを変更しました。ほかの PC・ほかの人には、新しいマスターパスワードを別の方法（口頭など）で伝えてください。");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Render($"保管ファイルに書き込めなかったので、変更していません: {Ferry.ViewModels.BrowserViewModel.Describe(e)}");
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
            {
                // 開いていない・鍵を保存できなかった など。状態が変わっていれば、その状態の欄を出す
                _changing = false;
                Render(e.Message);
            }
        };
        cancel.Click += (_, _) =>
        {
            _changing = false;
            Render();
        };
        Children.Add(current);
        Children.Add(first);
        Children.Add(again);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(change);
        buttons.Children.Add(cancel);
        Children.Add(buttons);
    }

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static StackPanel Status(string text, string glyph)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["FerryBlueBrush"] });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        return panel;
    }

    /// <summary>押すと確認のフライアウトを出すボタン（設定画面の上に別のダイアログは出せないため）</summary>
    private static Button ConfirmButton(string text, string message, string action, Action confirmed)
    {
        var button = new Button { Content = text };
        var confirm = new Button { Content = action, Margin = new Thickness(0, 8, 0, 0) };
        var flyoutContent = new StackPanel { MaxWidth = 300 };
        flyoutContent.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        flyoutContent.Children.Add(confirm);
        var flyout = new Flyout { Content = flyoutContent };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            confirmed();
        };
        button.Flyout = flyout;
        return button;
    }
}

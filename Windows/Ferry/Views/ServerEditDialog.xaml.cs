using Ferry.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.Views;

/// <summary>接続先の追加・編集</summary>
public sealed partial class ServerEditDialog : ContentDialog
{
    private sealed record FolderChoice(Guid? Id, string Name);

    private static readonly ServerProtocol[] Protocols =
        [ServerProtocol.Ftp, ServerProtocol.Ftps, ServerProtocol.FtpsImplicit, ServerProtocol.Sftp];

    private readonly ServerConfig _original;
    private readonly bool _isNew;

    /// <summary>保存した接続先（キャンセルなら null）</summary>
    public ServerConfig? Result { get; private set; }

    public ServerEditDialog(XamlRoot root, ServerConfig? server, Guid? folderId = null, bool shared = false)
    {
        InitializeComponent();
        XamlRoot = root;
        _isNew = server is null;
        _original = server ?? new ServerConfig { FolderId = folderId };
        var store = AppServices.Store;
        Title = _isNew ? "接続先を追加" : "接続設定";

        foreach (var protocol in Protocols) ProtocolBox.Items.Add(protocol.DisplayName());
        ProtocolBox.SelectedIndex = Array.IndexOf(Protocols, _original.ServerProtocol);

        NameBox.Text = _original.Name;
        HostBox.Text = _original.Host;
        UserBox.Text = _original.Username;
        if (_original.Port is { } port) PortBox.Value = port;
        RemotePathBox.Text = _original.InitialPath ?? "";
        LocalPathBox.Text = _original.LocalPath ?? "";
        PrivateKeyBox.Text = _original.PrivateKeyPath ?? "";
        PassiveSwitch.IsOn = _original.PassiveMode;
        VerifySwitch.IsOn = _original.VerifyCertificate;
        if (!_isNew && AppServices.Passwords.GetPassword(_original.Id) is not null)
            PasswordInput.PlaceholderText = "保存済み（変えるときだけ入力）";

        // 共有するかは新しい接続先のときだけ選ぶ（既存のものはサイドバーの右クリックで移す）
        SharedCheck.Visibility = _isNew && store.Shared.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        SharedCheck.IsChecked = _isNew ? shared && store.Shared.IsEnabled : store.IsShared(_original.Id);
        UpdateFolders();
        UpdateProtocolFields();
        IsPrimaryButtonEnabled = HostBox.Text.Trim().Length > 0;
    }

    private ServerProtocol SelectedProtocol => Protocols[Math.Max(ProtocolBox.SelectedIndex, 0)];
    private bool IsShared => SharedCheck.IsChecked == true;

    private void UpdateFolders()
    {
        var store = AppServices.Store;
        var folders = IsShared ? store.Shared.Folders : store.PersonalFolders;
        var choices = new List<FolderChoice> { new(null, "フォルダなし") };
        choices.AddRange(folders.Select(f => new FolderChoice(f.Id, f.Name)));
        FolderBox.ItemsSource = choices;
        FolderBox.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Id == _original.FolderId));
    }

    private void UpdateProtocolFields()
    {
        var protocol = SelectedProtocol;
        PortBox.PlaceholderText = protocol.DefaultPort().ToString();
        PassiveSwitch.Visibility = protocol.IsFtpFamily() ? Visibility.Visible : Visibility.Collapsed;
        VerifySwitch.Visibility = protocol.UsesTls() ? Visibility.Visible : Visibility.Collapsed;
        PrivateKeyRow.Visibility = protocol == ServerProtocol.Sftp ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProtocolFields();
    private void SharedCheck_Changed(object sender, RoutedEventArgs e) => UpdateFolders();
    private void Required_TextChanged(object sender, TextChangedEventArgs e) => IsPrimaryButtonEnabled = HostBox.Text.Trim().Length > 0;

    private async void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow!.AppWindow.Id);
        picker.FileTypeFilter.Add("*");
        var result = await picker.PickSingleFileAsync();
        if (result is not null) PrivateKeyBox.Text = result.Path;
    }

    private async void BrowseLocal_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.MainWindow!.AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        if (result is not null) LocalPathBox.Text = result.Path;
    }

    private void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var host = HostBox.Text.Trim();
        var protocol = SelectedProtocol;
        var port = double.IsNaN(PortBox.Value) ? (int?)null : (int)PortBox.Value;
        var server = _original with
        {
            Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : host,
            ServerProtocol = protocol,
            Host = host,
            // 既定のポートと同じなら書かない（方式を変えたときに付いてこないように）
            Port = port == protocol.DefaultPort() ? null : port,
            Username = UserBox.Text.Trim(),
            InitialPath = Empty(RemotePathBox.Text),
            LocalPath = Empty(LocalPathBox.Text),
            PrivateKeyPath = protocol == ServerProtocol.Sftp ? Empty(PrivateKeyBox.Text) : null,
            PassiveMode = PassiveSwitch.IsOn,
            VerifyCertificate = VerifySwitch.IsOn,
            FolderId = (FolderBox.SelectedItem as FolderChoice)?.Id,
        };

        var store = AppServices.Store;
        if (_isNew || store.IsShared(server.Id) == IsShared)
        {
            store.Upsert(server, shared: IsShared);
        }
        else
        {
            store.MoveServer(server.Id, server.FolderId, IsShared);
            store.Upsert(server);
        }
        // 保存しない場合は、接続するときにパスワード入力画面で聞く
        if (PasswordInput.Password.Length > 0 && SavePasswordCheck.IsChecked == true)
            AppServices.Passwords.SetPassword(server.Id, PasswordInput.Password);
        else if (SavePasswordCheck.IsChecked == false)
            AppServices.Passwords.SetPassword(server.Id, null);
        Result = server;
    }

    private static string? Empty(string text) => text.Trim().Length > 0 ? text.Trim() : null;
}

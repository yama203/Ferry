using System.Globalization;
using Ferry.Core;

namespace Ferry.Models;

/// <summary>ローカル・リモート共通のファイル表示用データ</summary>
public sealed class FileEntry
{
    public required string Name { get; init; }
    /// <summary>ローカルは C:\... の形、リモートは /... の形</summary>
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsSymlink { get; init; }
    /// <summary>「この PC」に並べるドライブ</summary>
    public bool IsDrive { get; init; }
    public long? Size { get; init; }
    /// <summary>更新日時（UTC）</summary>
    public DateTime? Modified { get; init; }
    /// <summary>"rwxr-xr-x" 形式（サーバー側のみ）</summary>
    public string? Permissions { get; init; }
    public bool IsHidden { get; init; }

    public static FileEntry FromRemote(RemoteItem item) => new()
    {
        Name = item.Name,
        Path = item.Path,
        IsDirectory = item.IsDirectory,
        IsSymlink = item.Kind == RemoteItem.ItemKind.Symlink,
        Size = item.Size,
        Modified = item.Modified,
        Permissions = item.Permissions,
        IsHidden = item.Name.StartsWith('.'),
    };

    public static FileEntry FromLocal(FileSystemInfo info)
    {
        var isLink = info.LinkTarget is not null;
        return new FileEntry
        {
            Name = info.Name,
            Path = info.FullName,
            IsDirectory = info is DirectoryInfo,
            IsSymlink = isLink,
            Size = info is FileInfo file ? file.Length : null,
            Modified = info.LastWriteTimeUtc,
            IsHidden = info.Name.StartsWith('.') || info.Attributes.HasFlag(FileAttributes.Hidden),
        };
    }

    // 表示用

    public string Glyph =>
        IsDrive ? "\uEDA2" : IsDirectory ? "\uE8B7" : IsSymlink ? "\uE71B" : "\uE8A5";

    public string SizeText => IsDirectory || Size is null ? "--" : FormatSize(Size.Value);

    public string DateText => Modified is { } date
        ? date.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture)
        : "--";

    public string PermissionsText => Permissions ?? "";

    public double Opacity => IsHidden ? 0.6 : 1;

    // 並べ替え用
    public long SortSize => IsDirectory ? -1 : Size ?? 0;
    public DateTime SortDate => Modified ?? DateTime.MinValue;

    public static string FormatSize(long bytes)
    {
        string[] units = ["バイト", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} {units[0]}" : $"{value:0.#} {units[unit]}";
    }
}

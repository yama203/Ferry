using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Ferry.Models;

/// <summary>
/// エクスプローラーと同じアイコン（フォルダは黄色、ファイルは関連付けられたアプリのもの）。
/// サーバーのファイルも拡張子から決める。拡張子ごとに 1 回だけ作って使い回す。UI スレッドで使う。
/// </summary>
public static class ShellIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>拡張子だけでは決まらず、ファイルごとにアイコンが違うもの（ローカルのみ、そのファイルから取る）</summary>
    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url" };

    public static ImageSource? For(FileEntry entry, bool isRemote)
    {
        string key, path;
        uint attributes;
        var useAttributes = true;
        if (entry.IsDrive)
        {
            // ドライブは種類（ローカル・ネットワーク・USB など）でアイコンが違うので、そのものから取る
            key = "drive:" + entry.Path;
            path = entry.Path;
            attributes = 0;
            useAttributes = false;
        }
        else if (entry.IsDirectory)
        {
            key = "folder";
            path = "folder";
            attributes = FileAttributeDirectory;
        }
        else
        {
            var extension = Path.GetExtension(entry.Name);
            if (!isRemote && PerFileExtensions.Contains(extension))
            {
                key = "file:" + entry.Path;
                path = entry.Path;
                useAttributes = false;
            }
            else
            {
                key = "ext:" + extension;
                path = "file" + extension;
            }
            attributes = FileAttributeNormal;
        }
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var icon = Load(path, attributes, useAttributes);
        Cache[key] = icon;
        return icon;
    }

    private static WriteableBitmap? Load(string path, uint attributes, bool useAttributes)
    {
        var info = new ShFileInfo();
        var flags = ShgfiIcon | ShgfiLargeIcon | (useAttributes ? ShgfiUseFileAttributes : 0);
        if (SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            return ToBitmap(info.hIcon);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>HICON を WinUI の画像にする（32 ビットの BGRA。透明度が無い古いアイコンはマスクから作る）</summary>
    private static WriteableBitmap? ToBitmap(IntPtr icon)
    {
        if (!GetIconInfo(icon, out var iconInfo)) return null;
        try
        {
            if (GetObject(iconInfo.hbmColor, Marshal.SizeOf<Bitmap>(), out var bitmap) == 0) return null;
            int width = bitmap.bmWidth, height = bitmap.bmHeight;
            var color = ReadPixels(iconInfo.hbmColor, width, height);
            if (color is null) return null;

            var hasAlpha = false;
            for (var i = 3; i < color.Length; i += 4)
            {
                if (color[i] != 0) { hasAlpha = true; break; }
            }
            if (!hasAlpha && ReadPixels(iconInfo.hbmMask, width, height) is { } mask)
            {
                // マスクが黒（0）のところが見える部分
                for (var i = 0; i < color.Length; i += 4) color[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
            }
            // WriteableBitmap は「透明度を掛けた」BGRA
            for (var i = 0; i < color.Length; i += 4)
            {
                var alpha = color[i + 3];
                if (alpha == 255) continue;
                color[i] = (byte)(color[i] * alpha / 255);
                color[i + 1] = (byte)(color[i + 1] * alpha / 255);
                color[i + 2] = (byte)(color[i + 2] * alpha / 255);
            }
            var result = new WriteableBitmap(width, height);
            using (var stream = result.PixelBuffer.AsStream()) stream.Write(color, 0, color.Length);
            result.Invalidate();
            return result;
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
        }
    }

    private static byte[]? ReadPixels(IntPtr hBitmap, int width, int height)
    {
        if (hBitmap == IntPtr.Zero) return null;
        var header = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height, // 上から下へ
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            return GetDIBits(dc, hBitmap, 0, (uint)height, pixels, ref header, 0) == 0 ? null : pixels;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    // MARK: Win32

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        // BITMAPINFO の色テーブルの分（32 ビットでは使わないが、余裕を持たせる）
        public uint bmiColors0;
        public uint bmiColors1;
        public uint bmiColors2;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out Bitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}

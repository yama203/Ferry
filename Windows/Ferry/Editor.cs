using System.Diagnostics;

namespace Ferry;

/// <summary>ファイルを開くエディタ。設定が無ければ VS Code（入っていれば）、それも無ければ既定のアプリ</summary>
public static class Editor
{
    public static string? Path
    {
        get
        {
            if (AppServices.Settings.EditorPath is { } configured && File.Exists(configured)) return configured;
            return FindVisualStudioCode();
        }
    }

    public static string DisplayName => Path is { } path
        ? FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } name ? name : System.IO.Path.GetFileNameWithoutExtension(path)
        : "既定のアプリ";

    public static string? FindVisualStudioCode()
    {
        string[] candidates =
        [
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static void Open(string file)
    {
        if (Path is { } editor)
        {
            Process.Start(new ProcessStartInfo(editor) { ArgumentList = { file }, UseShellExecute = false });
        }
        else
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
    }
}

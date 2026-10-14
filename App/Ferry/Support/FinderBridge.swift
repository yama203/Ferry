import Foundation

/// Finder とのやりとり（AppleScript）
enum FinderBridge {
    struct BridgeError: LocalizedError {
        let errorDescription: String?
    }

    /// Finder の一番手前のウィンドウで開いているフォルダ。初回は macOS が Finder の操作を許可するか確認する。
    static func frontWindowFolder() -> Result<String, Error> {
        let source = """
        tell application "Finder"
            if (count of Finder windows) is 0 then return ""
            return POSIX path of (target of front Finder window as alias)
        end tell
        """
        var errorInfo: NSDictionary?
        guard let result = NSAppleScript(source: source)?.executeAndReturnError(&errorInfo) else {
            let number = errorInfo?[NSAppleScript.errorNumber] as? Int
            let message = number == -1743
                ? "Finder を操作する許可がありません。「システム設定」→「プライバシーとセキュリティ」→「オートメーション」で Ferry の Finder をオンにしてください"
                : (errorInfo?[NSAppleScript.errorMessage] as? String ?? "Finder のフォルダを取得できませんでした")
            return .failure(BridgeError(errorDescription: message))
        }
        guard let path = result.stringValue, !path.isEmpty else {
            return .failure(BridgeError(errorDescription: "Finder のウィンドウが開いていません"))
        }
        return .success(path)
    }
}

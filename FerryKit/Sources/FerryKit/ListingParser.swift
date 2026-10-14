import Foundation

/// サーバーが返すディレクトリ一覧（MLSD / ls -l 形式）を RemoteItem に変換する。
public enum ListingParser {
    public static func decode(_ data: Data) -> String {
        String(data: data, encoding: .utf8)
            ?? String(data: data, encoding: .shiftJIS)
            ?? String(decoding: data, as: UTF8.self)
    }

    // MARK: - MLSD

    /// 例: `type=file;size=1234;modify=20261014103000;UNIX.mode=0644; index.php`
    public static func parseMLSD(_ text: String, directory: String) -> [RemoteItem] {
        text.split(whereSeparator: \.isNewline).compactMap { parseMLSDLine(String($0), directory: directory) }
    }

    static func parseMLSDLine(_ line: String, directory: String) -> RemoteItem? {
        guard let space = line.firstIndex(of: " ") else { return nil }
        let name = String(line[line.index(after: space)...])
        guard !name.isEmpty, name != ".", name != ".." else { return nil }

        var facts: [String: String] = [:]
        for fact in line[..<space].split(separator: ";") {
            let pair = fact.split(separator: "=", maxSplits: 1)
            guard pair.count == 2 else { continue }
            facts[pair[0].lowercased()] = String(pair[1])
        }

        let kind: RemoteItem.Kind
        switch facts["type"]?.lowercased() {
        case "dir": kind = .directory
        case "cdir", "pdir": return nil
        case let type? where type.contains("slink") || type.contains("symlink"): kind = .symlink
        default: kind = .file
        }

        return RemoteItem(
            name: name,
            path: RemotePath.join(directory, name),
            kind: kind,
            size: facts["size"].flatMap { Int64($0) },
            modified: facts["modify"].flatMap(parseMLSDTime),
            permissions: facts["unix.mode"].flatMap(permissionString(fromOctal:))
        )
    }

    private static let mlsdFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.dateFormat = "yyyyMMddHHmmss"
        return formatter
    }()

    static func parseMLSDTime(_ value: String) -> Date? {
        mlsdFormatter.date(from: String(value.prefix(14)))
    }

    static func permissionString(fromOctal octal: String) -> String? {
        guard let mode = Int(octal, radix: 8) else { return nil }
        let symbols: [Character] = ["r", "w", "x"]
        return String((0..<9).map { bit in
            mode & (1 << (8 - bit)) != 0 ? symbols[bit % 3] : "-"
        })
    }

    // MARK: - ls -l 形式（FTP の LIST、SFTP の一覧）

    /// 例: `drwxr-xr-x    2 user  group     4096 Oct 14 10:00 wp-content`
    public static func parseUnixListing(_ text: String, directory: String, now: Date = Date()) -> [RemoteItem] {
        text.split(whereSeparator: \.isNewline).compactMap { parseUnixLine(String($0), directory: directory, now: now) }
    }

    private static let months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"]

    static func parseUnixLine(_ line: String, directory: String, now: Date) -> RemoteItem? {
        guard let typeChar = line.first, "-dlbcps".contains(typeChar) else { return nil }

        // 空白区切りのトークンと、その終端位置を集める（ファイル名の空白を保つため）
        var tokens: [(text: Substring, end: String.Index)] = []
        var index = line.startIndex
        while index < line.endIndex, tokens.count < 9 {
            while index < line.endIndex, line[index].isWhitespace { index = line.index(after: index) }
            let start = index
            while index < line.endIndex, !line[index].isWhitespace { index = line.index(after: index) }
            if start < index { tokens.append((line[start..<index], index)) }
        }

        // 月名の位置を探す（グループ欄が無いサーバーもあるため固定位置にしない）
        guard let monthIndex = tokens.indices.first(where: { i in
            i >= 3 && i + 2 < tokens.count
                && months.contains(tokens[i].text.lowercased())
                && Int(tokens[i + 1].text) != nil
        }) else { return nil }

        let nameStart = tokens[monthIndex + 2].end
        var name = String(line[nameStart...].drop(while: { $0 == " " }))
        guard !name.isEmpty, name != ".", name != ".." else { return nil }

        let kind: RemoteItem.Kind
        var target: String?
        switch typeChar {
        case "d": kind = .directory
        case "l":
            kind = .symlink
            if let arrow = name.range(of: " -> ") {
                target = String(name[arrow.upperBound...])
                name = String(name[..<arrow.lowerBound])
            }
        default: kind = .file
        }

        let permissions = String(tokens[0].text.dropFirst().prefix(9))
        return RemoteItem(
            name: name,
            path: RemotePath.join(directory, name),
            kind: kind,
            size: Int64(tokens[monthIndex - 1].text),
            modified: parseUnixDate(
                month: String(tokens[monthIndex].text),
                day: String(tokens[monthIndex + 1].text),
                timeOrYear: String(tokens[monthIndex + 2].text),
                now: now
            ),
            permissions: permissions.count == 9 ? permissions : nil,
            symlinkTarget: target
        )
    }

    static func parseUnixDate(month: String, day: String, timeOrYear: String, now: Date) -> Date? {
        guard let monthNumber = months.firstIndex(of: month.lowercased()).map({ $0 + 1 }),
              let dayNumber = Int(day) else { return nil }
        let calendar = Calendar(identifier: .gregorian)
        var components = DateComponents(month: monthNumber, day: dayNumber)

        if timeOrYear.contains(":") {
            let parts = timeOrYear.split(separator: ":")
            components.hour = Int(parts[0])
            components.minute = parts.count > 1 ? Int(parts[1]) : 0
            // 年が省略されている場合は直近 1 年以内の日付。未来になるなら前年。
            components.year = calendar.component(.year, from: now)
            if let date = calendar.date(from: components), date > now.addingTimeInterval(86_400) {
                components.year! -= 1
            }
        } else {
            components.year = Int(timeOrYear)
        }
        return calendar.date(from: components)
    }
}

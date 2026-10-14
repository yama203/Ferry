import Foundation
import Testing
@testable import FerryKit

struct ListingParserTests {
    @Test func mlsd() {
        let text = """
        type=cdir;modify=20261014000000;UNIX.mode=0755; .
        type=dir;modify=20261014103000;UNIX.mode=0755; wp-content
        type=file;size=405;modify=20261001120000;UNIX.mode=0644; index.php
        type=file;size=12;modify=20261001120000; 日本語 ファイル.txt
        """
        let items = ListingParser.parseMLSD(text, directory: "/public_html")
        #expect(items.count == 3)
        #expect(items[0].name == "wp-content")
        #expect(items[0].isDirectory)
        #expect(items[0].permissions == "rwxr-xr-x")
        #expect(items[1].path == "/public_html/index.php")
        #expect(items[1].size == 405)
        #expect(items[2].name == "日本語 ファイル.txt")
    }

    @Test func unixListing() {
        let now = ISO8601DateFormatter().date(from: "2026-10-14T12:00:00Z")!
        let text = """
        total 12
        drwxr-xr-x    2 user  group     4096 Oct 14 10:00 wp-content
        -rw-r--r--    1 user  group      405 Mar  3  2025 index.php
        lrwxrwxrwx    1 user  group       11 Dec 31 23:59 www -> public_html
        -rw-r--r--    1 user           9 Jan  1 00:00 my file.txt
        drwxr-xr-x    3 user  group     4096 Oct 14 10:00 .
        """
        let items = ListingParser.parseUnixListing(text, directory: "/", now: now)
        #expect(items.count == 4)
        #expect(items[0].isDirectory)
        #expect(items[0].path == "/wp-content")
        #expect(items[1].size == 405)
        #expect(Calendar(identifier: .gregorian).component(.year, from: items[1].modified!) == 2025)
        #expect(items[2].kind == .symlink)
        #expect(items[2].name == "www")
        #expect(items[2].symlinkTarget == "public_html")
        // 12/31 は未来になるので前年扱い
        #expect(Calendar(identifier: .gregorian).component(.year, from: items[2].modified!) == 2025)
        // グループ欄が無い形式、名前に空白
        #expect(items[3].name == "my file.txt")
        #expect(items[3].size == 9)
    }

    @Test func paths() {
        #expect(RemotePath.join("/", "a") == "/a")
        #expect(RemotePath.join("/a", "b") == "/a/b")
        #expect(RemotePath.parent(of: "/a/b") == "/a")
        #expect(RemotePath.parent(of: "/a") == "/")
    }

    @Test func urls() {
        let ftp = RemoteClient(server: ServerConfig(name: "t", serverProtocol: .ftps, host: "example.com",
                                                    port: 10021, username: "u"), password: nil)
        #expect(ftp.url(for: "/", directory: true) == "ftp://example.com:10021/%2F/")
        #expect(ftp.url(for: "/public_html/日本", directory: true)
            == "ftp://example.com:10021/%2Fpublic_html/%E6%97%A5%E6%9C%AC/")

        let sftp = RemoteClient(server: ServerConfig(name: "t", serverProtocol: .sftp, host: "example.com",
                                                     username: "u"), password: nil)
        #expect(sftp.url(for: "/home/u/a b.txt", directory: false) == "sftp://example.com:22/home/u/a%20b.txt")
    }
}

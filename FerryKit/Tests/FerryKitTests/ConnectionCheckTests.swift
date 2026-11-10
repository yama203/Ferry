import CCurl
import Foundation
import Testing
@testable import FerryKit

struct FTPSCheckTests {
    private func error(_ code: CURLcode, detail: String? = nil) -> FerryError {
        FerryError.from(code: code, detail: detail)
    }

    @Test func classifiesCurlErrors() {
        #expect(FTPSCheck.classify(error(CURLE_USE_SSL_FAILED)) == .unsupported)
        #expect(FTPSCheck.classify(error(CURLE_PEER_FAILED_VERIFICATION)) == .certificate)
        #expect(FTPSCheck.classify(error(CURLE_LOGIN_DENIED))
                == .failed("ログインできませんでした（ユーザー名かパスワードが違うかもしれません）"))
        #expect(FTPSCheck.classify(error(CURLE_SSL_CONNECT_ERROR, detail: "x")) == .failed("暗号化の接続（TLS）に失敗しました。x"))
        #expect(FTPSCheck.classify(error(CURLE_ABORTED_BY_CALLBACK)) == .failed("中止しました"))
        if case .failed(let reason) = FTPSCheck.classify(error(CURLE_COULDNT_CONNECT)) {
            #expect(reason.hasPrefix("サーバーに接続できません"))
        } else {
            Issue.record("つながらないときは調べられなかったことにする")
        }
    }

    @Test func classifiesErrorsWithoutCurlCode() {
        #expect(FTPSCheck.classify(FerryError(kind: .untrustedCertificate, message: "m", detail: nil)) == .certificate)
        #expect(FTPSCheck.classify(FerryError(kind: .other, message: "m", detail: nil)) == .failed("m"))
    }

    @Test func ftpsConfigKeepsPortAndAccount() {
        let ftp = ServerConfig(name: "n", serverProtocol: .ftp, host: "example.jp", port: nil, username: "u")
        let ftps = FTPSCheck.ftpsConfig(ftp, verifyCertificate: false)
        #expect(ftps.serverProtocol == .ftps)
        #expect(ftps.port == nil && ftps.effectivePort == 21)
        #expect(ftps.verifyCertificate == false)
        #expect(ftps.host == "example.jp" && ftps.username == "u" && ftps.id == ftp.id)
    }

    @Test func checksOnlyPlainFTPWithPassword() async {
        let sftp = ServerConfig(name: "n", serverProtocol: .sftp, host: "example.jp", username: "u")
        #expect(await FTPSCheck.check(sftp, password: "p") == .failed(reason: "暗号化なしの FTP の接続先ではありません"))
        let ftp = ServerConfig(name: "n", serverProtocol: .ftp, host: "example.jp", username: "u")
        #expect(await FTPSCheck.check(ftp, password: nil) == .failed(reason: "パスワードが保存されていないので調べられません"))
    }

    @Test func parsesCertificateNames() {
        let text = "Subject:CN = sv1234.xserver.jp\nX509v3 Subject Alternative Name:DNS:sv1234.xserver.jp, DNS:*.xserver.jp, IP Address:127.0.0.1\n"
        #expect(CurlHandle.parseCertificateNames(text) == ["sv1234.xserver.jp", "*.xserver.jp"])
        #expect(CurlHandle.parseCertificateNames("Subject:C=JP, O=Example, CN=ftp.example.jp\n") == ["ftp.example.jp"])
        #expect(CurlHandle.parseCertificateNames("") == [])
    }
}

struct HostKeyTests {
    /// scratch の sshd のホスト鍵（ssh-keygen -lf で SHA256:O0jXd8SxMzcRnSHhEydmEimzGx0tmSuZ+/PLVxYfePc と出るもの）
    private let ed25519 = "AAAAC3NzaC1lZDI1NTE5AAAAIIsFTQAsMn9oiJ31mg9sbsNO67khZXjNrRjkChlXP9Vz"

    @Test func fingerprintMatchesOpenSSH() {
        let key = HostKey(data: Data(base64Encoded: ed25519)!)
        #expect(key.algorithm == "ssh-ed25519")
        #expect(key.typeName == "ED25519")
        #expect(key.fingerprint.hasPrefix("SHA256:"))
        #expect(!key.fingerprint.hasSuffix("="))
        #expect(key.fingerprint == "SHA256:O0jXd8SxMzcRnSHhEydmEimzGx0tmSuZ+/PLVxYfePc")
    }

    @Test func malformedKeyHasUnknownAlgorithm() {
        #expect(HostKey(data: Data([0, 0])).algorithm == "不明")
        #expect(HostKey(data: Data([0, 0, 0, 200, 1])).algorithm == "不明")
    }

    @Test func hostPattern() {
        #expect(KnownHosts.hostPattern(host: "example.jp", port: 22) == "example.jp")
        #expect(KnownHosts.hostPattern(host: "example.jp", port: 2222) == "[example.jp]:2222")
    }

    @Test func removesOnlyThatHost() {
        let contents = """
        # コメント
        127.0.0.1 ssh-ed25519 AAAAold
        [127.0.0.1]:2222 ssh-ed25519 AAAAold2
        [127.0.0.1]:2200 ssh-ed25519 AAAAotherport
        example.jp ssh-rsa AAAAother
        Example.JP,127.0.0.1 ecdsa-sha2-nistp256 AAAAboth
        |1|abc=|def= ssh-ed25519 AAAAhashed
        @revoked 127.0.0.1 ssh-ed25519 AAAArevoked

        """
        let updated = KnownHosts.removingEntries(for: "127.0.0.1", port: 2222, from: contents)
        #expect(updated == """
        # コメント
        [127.0.0.1]:2200 ssh-ed25519 AAAAotherport
        example.jp ssh-rsa AAAAother
        Example.JP ecdsa-sha2-nistp256 AAAAboth
        |1|abc=|def= ssh-ed25519 AAAAhashed
        @revoked 127.0.0.1 ssh-ed25519 AAAArevoked

        """)
    }

    @Test func removesPlainHostOnDefaultPortCaseInsensitively() {
        let contents = "SFTP.example.jp ssh-ed25519 AAAAold\n[sftp.example.jp]:2222 ssh-ed25519 AAAAkeep\n"
        #expect(KnownHosts.removingEntries(for: "sftp.example.jp", port: 22, from: contents)
                == "[sftp.example.jp]:2222 ssh-ed25519 AAAAkeep\n")
    }

    @Test func keepsApprovedKey() {
        let key = HostKey(data: Data(base64Encoded: ed25519)!)
        let contents = "127.0.0.1 ssh-ed25519 AAAAold\n127.0.0.1 ssh-ed25519 \(ed25519)\n"
        #expect(KnownHosts.removingEntries(for: "127.0.0.1", port: 22, from: contents, keepingKey: key)
                == "127.0.0.1 ssh-ed25519 \(ed25519)\n")
    }

    @Test func removesFromFile() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("ferry-known_hosts-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: url) }
        try KnownHosts.removeEntries(for: "a", port: 22, in: url)
        #expect(!FileManager.default.fileExists(atPath: url.path))
        try Data("a ssh-ed25519 AAAA\nb ssh-ed25519 BBBB\n".utf8).write(to: url)
        try KnownHosts.removeEntries(for: "a", port: 22, in: url)
        #expect(try String(contentsOf: url, encoding: .utf8) == "b ssh-ed25519 BBBB\n")
    }

    @Test func hostKeyChangedErrorCarriesKey() {
        let key = HostKey(data: Data(base64Encoded: ed25519)!)
        let error = FerryError.hostKeyChanged(key)
        #expect(error.kind == .hostKeyChanged)
        #expect(error.hostKey == key)
        #expect(error.detail?.contains(key.fingerprint) == true)
    }
}

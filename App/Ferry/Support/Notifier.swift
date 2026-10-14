import Foundation
import UserNotifications

/// macOS の通知センターへの通知
enum Notifier {
    private static let delegate = ForegroundDelegate()

    /// 起動時に呼ぶ。Ferry が前面にあるときも通知を表示する。
    static func setUp() {
        UNUserNotificationCenter.current().delegate = delegate
    }

    /// 初めて使う場面で許可を求める（2 回目以降は何も出ない）
    static func requestAuthorization() async {
        _ = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound])
    }

    static func post(title: String, body: String) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request)
    }

    private final class ForegroundDelegate: NSObject, UNUserNotificationCenterDelegate {
        func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                    withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
            completionHandler([.banner, .sound])
        }
    }
}

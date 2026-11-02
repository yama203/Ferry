import Foundation
import Observation

/// 最後に接続できなかった接続先と、その理由。サイドバーに印を出し、つながったら消す。
/// この Mac だけの記録なので、接続先の一覧（共有・同期される）ではなく UserDefaults に置く（Windows 版の connectionProblems と同じ）
@MainActor @Observable
final class ConnectionProblems {
    static let shared = ConnectionProblems()

    private(set) var problems: [UUID: String]
    private static let key = "connectionProblems"

    private init() {
        let stored = UserDefaults.standard.dictionary(forKey: Self.key) as? [String: String] ?? [:]
        problems = Dictionary(uniqueKeysWithValues: stored.compactMap { key, value in UUID(uuidString: key).map { ($0, value) } })
    }

    subscript(id: UUID) -> String? { problems[id] }

    /// 接続できなかった理由を覚える（nil で消す）
    func set(_ problem: String?, for id: UUID) {
        guard problems[id] != problem else { return }
        problems[id] = problem
        UserDefaults.standard.set(Dictionary(uniqueKeysWithValues: problems.map { ($0.key.uuidString, $0.value) }), forKey: Self.key)
    }
}

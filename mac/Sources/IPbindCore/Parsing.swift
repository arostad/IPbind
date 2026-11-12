import Foundation

public enum Parse {
    /// Reads `networksetup -listnetworkserviceorder`.
    public static func serviceOrder(_ text: String) -> [NetworkService] {
        var result: [NetworkService] = []
        var pending: (name: String, enabled: Bool)?
        for raw in text.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if let m = line.firstMatch(#"^\((\d+|\*)\)\s+(.+)$"#) {
                pending = (m[2], m[1] != "*")
            } else if let p = pending, let m = line.firstMatch(#"^\(Hardware Port:\s*(.*?),\s*Device:\s*(.*?)\)$"#) {
                result.append(NetworkService(name: p.name, hardwarePort: m[1], device: m[2], enabled: p.enabled))
                pending = nil
            }
        }
        return result
    }

    public struct InterfaceInfo {
        public var addresses: [(ip: IPv4, mask: IPv4)] = []
        public var status: String?   // "active" / "inactive"
        public init() {}
    }

    /// Reads `ifconfig <dev>`.
    public static func ifconfig(_ text: String) -> InterfaceInfo {
        var info = InterfaceInfo()
        for raw in text.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if let m = line.firstMatch(#"^inet (\S+) netmask 0x([0-9a-fA-F]{8})"#),
               let ip = IPv4(m[1]), let mv = UInt32(m[2], radix: 16) {
                info.addresses.append((ip, IPv4(mv)))
            } else if let m = line.firstMatch(#"^status:\s*(\S+)"#) {
                info.status = m[1]
            }
        }
        return info
    }
}

extension String {
    /// Capture groups of the first match (index 0 is the whole match).
    func firstMatch(_ pattern: String) -> [String]? {
        guard let re = try? NSRegularExpression(pattern: pattern),
              let m = re.firstMatch(in: self, range: NSRange(startIndex..., in: self)) else { return nil }
        return (0..<m.numberOfRanges).map {
            Range(m.range(at: $0), in: self).map { String(self[$0]) } ?? ""
        }
    }
}

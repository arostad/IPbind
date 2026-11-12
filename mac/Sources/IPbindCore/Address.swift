import Foundation

/// An IPv4 address held as a number, so any text built from it is digits and dots only.
public struct IPv4: Hashable, Comparable, CustomStringConvertible {
    public let value: UInt32

    public init(_ value: UInt32) { self.value = value }

    /// Strict dotted-quad parse: four 1-3 digit octets, each 0-255. Surrounding spaces are allowed.
    public init?(_ text: String) {
        let parts = text.trimmingCharacters(in: .whitespaces).split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        var v: UInt32 = 0
        for p in parts {
            guard (1...3).contains(p.count), p.allSatisfy({ $0.isASCII && $0.isNumber }),
                  let o = UInt32(p), o <= 255 else { return nil }
            v = (v << 8) | o
        }
        value = v
    }

    public var description: String {
        "\((value >> 24) & 0xFF).\((value >> 16) & 0xFF).\((value >> 8) & 0xFF).\(value & 0xFF)"
    }

    /// Usable as a host, gateway or DNS server: not 0.x, loopback, multicast/reserved.
    public var isUsableHost: Bool {
        let first = value >> 24
        return first != 0 && first != 127 && first < 224
    }

    public static func mask(prefix: Int) -> IPv4 {
        IPv4(prefix == 0 ? 0 : (UInt32.max << UInt32(32 - prefix)))
    }

    public static func < (a: IPv4, b: IPv4) -> Bool { a.value < b.value }
}

/// One "ip/prefix" entry from the address list. A missing prefix means /24, as on Windows.
public struct CidrEntry: Hashable, CustomStringConvertible {
    public let ip: IPv4
    public let prefix: Int

    public init(ip: IPv4, prefix: Int) { self.ip = ip; self.prefix = prefix }

    public init?(_ text: String) {
        let t = text.trimmingCharacters(in: .whitespaces)
        let pieces = t.split(separator: "/", omittingEmptySubsequences: false)
        guard pieces.count == 1 || pieces.count == 2, let ip = IPv4(String(pieces[0])) else { return nil }
        var prefix = 24
        if pieces.count == 2 {
            let p = pieces[1].trimmingCharacters(in: .whitespaces)
            guard (1...2).contains(p.count), p.allSatisfy({ $0.isASCII && $0.isNumber }),
                  let n = Int(p), (0...32).contains(n) else { return nil }
            prefix = n
        }
        self.ip = ip
        self.prefix = prefix
    }

    public var mask: IPv4 { IPv4.mask(prefix: prefix) }
    public var network: IPv4 { IPv4(ip.value & mask.value) }
    public var broadcast: IPv4 { IPv4(network.value | ~mask.value) }
    public var description: String { "\(ip)/\(prefix)" }

    public func contains(_ other: IPv4) -> Bool { (other.value & mask.value) == network.value }

    /// Why this entry can't be bound to an interface, or nil when it can.
    public var hostProblem: String? {
        if prefix == 0 { return "a /0 prefix covers every address" }
        if !ip.isUsableHost { return "\(ip) is not a usable host address" }
        if prefix <= 30 && (ip == network || ip == broadcast) {
            return "\(ip) is the \(ip == network ? "network" : "broadcast") address of \(network)/\(prefix)"
        }
        return nil
    }
}

public enum AddressList {
    public static let defaults = [
        "10.10.10.98/24", "10.255.255.98/24", "10.255.128.98/24",
        "192.168.0.98/24", "192.168.1.98/24", "192.168.2.98/24",
        "192.168.4.98/24", "192.168.255.98/24",
    ]

    public struct Parsed {
        public var entries: [CidrEntry] = []
        public var skipped: [(line: String, reason: String)] = []
    }

    /// Parses the list box: one entry per line, blank lines ignored, duplicates dropped.
    public static func parse(_ text: String) -> Parsed {
        var out = Parsed()
        var seen = Set<IPv4>()
        for raw in text.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty { continue }
            guard let e = CidrEntry(line) else { out.skipped.append((line, "not a valid IPv4/prefix")); continue }
            if let why = e.hostProblem { out.skipped.append((line, why)); continue }
            if seen.contains(e.ip) { out.skipped.append((line, "duplicate address")); continue }
            seen.insert(e.ip)
            out.entries.append(e)
        }
        return out
    }
}

import Foundation

/// How the static addresses are added. DHCP on the chosen service is never switched off.
public enum BindMode: String, CaseIterable, Identifiable {
    /// One extra manual network service per address on the same hardware port. Kept by macOS
    /// across reboots and replugs until Return to DHCP removes them.
    case persistent
    /// ifconfig aliases on the interface. Gone after unplug, link change, sleep/wake or reboot.
    case temporary
    public var id: String { rawValue }
}

/// A network service from `networksetup -listnetworkserviceorder`.
public struct NetworkService: Hashable, Identifiable {
    public let name: String
    public let hardwarePort: String
    public let device: String      // BSD name such as en5; empty for VPNs and the like
    public let enabled: Bool
    public var id: String { name }

    public init(name: String, hardwarePort: String, device: String, enabled: Bool) {
        self.name = name; self.hardwarePort = hardwarePort; self.device = device; self.enabled = enabled
    }

    /// Services IPbind creates are named "IPbind <device> <ip>".
    public var isIPbindService: Bool { name.hasPrefix(Names.servicePrefix) }

    /// Wired Ethernet-style ports: not Wi-Fi, Bluetooth, bridges, phones, VPNs or IPbind's own.
    public var looksWired: Bool {
        guard !device.isEmpty, !isIPbindService else { return false }
        let p = (hardwarePort + " " + name).lowercased()
        let skip = ["wi-fi", "wifi", "airport", "bluetooth", "bridge", "iphone", "ipad", "vpn", "firewire", "modem"]
        return !skip.contains(where: p.contains)
    }
}

public enum Names {
    public static let servicePrefix = "IPbind "
    public static func service(device: String, ip: IPv4) -> String { "\(servicePrefix)\(device) \(ip)" }
}

public enum Validate {
    /// BSD interface names: letters then digits, e.g. en5, bridge0.
    public static func device(_ s: String) -> Bool {
        s.range(of: #"^[a-z]{1,12}[0-9]{1,4}$"#, options: .regularExpression) != nil
    }

    /// Service and hardware-port names are quoted, never interpreted, but still refuse control
    /// characters, a leading "-" (networksetup would read it as an option) and silly lengths.
    public static func name(_ s: String) -> Bool {
        guard (1...128).contains(s.count), !s.hasPrefix("-"), s.trimmingCharacters(in: .whitespaces) == s else { return false }
        return !s.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) || $0 == "\u{2028}" || $0 == "\u{2029}" }
    }
}

public enum ShellQuote {
    /// POSIX single-quote quoting: the result is always one literal word.
    public static func word(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }
}

public enum AppleScript {
    /// Escapes text for an AppleScript string literal.
    public static func literal(_ s: String) -> String {
        "\"" + s.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "\n", with: "\\n") + "\""
    }

    /// One privileged step: macOS shows its own administrator password prompt.
    public static func adminShell(_ script: String) -> String {
        "do shell script " + literal(script) + " with administrator privileges"
    }
}

public struct ApplyPlan {
    public let service: NetworkService
    public let entries: [CidrEntry]
    public let gateway: GatewayPlan?
    public let mode: BindMode
    /// IPbind services already on this device, removed before the new ones are made.
    public let staleServices: [String]
    /// Addresses on the interface right now that IPbind may have added as aliases earlier.
    public let staleAliases: [IPv4]

    public init(service: NetworkService, entries: [CidrEntry], gateway: GatewayPlan?, mode: BindMode,
                staleServices: [String], staleAliases: [IPv4]) {
        self.service = service; self.entries = entries; self.gateway = gateway; self.mode = mode
        self.staleServices = staleServices; self.staleAliases = staleAliases
    }
}

public enum Commands {
    static let ns = "/usr/sbin/networksetup"
    static let ifc = "/sbin/ifconfig"

    static func check(_ service: NetworkService) throws {
        guard Validate.device(service.device) else { throw PlanError("The interface name '\(service.device)' is not valid.") }
        guard Validate.name(service.name), Validate.name(service.hardwarePort) else {
            throw PlanError("The network service name contains characters IPbind will not pass to networksetup.")
        }
    }

    static func line(_ words: String..., code: Int) -> String {
        words.joined(separator: " ") + " || exit \(code)"
    }

    static func removeAliases(_ ips: [IPv4], device: String) -> [String] {
        // Tolerant: an alias may already be gone after a replug.
        ips.map { "\(ifc) \(device) inet \($0) -alias 2>/dev/null; true" }
    }

    static func removeServices(_ names: [String]) throws -> [String] {
        try names.map {
            guard Validate.name($0), $0.hasPrefix(Names.servicePrefix) else { throw PlanError("Refusing to remove service '\($0)'.") }
            return line(ns, "-removenetworkservice", ShellQuote.word($0), code: 20)
        }
    }

    /// The shell script for Apply. Every value is either built from integers or single-quoted.
    public static func applyScript(_ p: ApplyPlan) throws -> String {
        try check(p.service)
        guard !p.entries.isEmpty else { throw PlanError("No valid addresses to apply.") }
        let dev = p.service.device
        var out = ["PATH=/usr/bin:/bin:/usr/sbin:/sbin"]
        out += try removeServices(p.staleServices)
        out += removeAliases(p.staleAliases, device: dev)

        switch p.mode {
        case .persistent:
            for e in p.entries {
                let name = Names.service(device: dev, ip: e.ip)
                let q = ShellQuote.word(name)
                out.append(line(ns, "-createnetworkservice", q, ShellQuote.word(p.service.hardwarePort), code: 21))
                if let g = p.gateway, g.entry == e {
                    out.append(line(ns, "-setmanual", q, "\(e.ip)", "\(e.mask)", "\(g.gateway)", code: 22))
                    let dns = g.dns.isEmpty ? "Empty" : g.dns.map(\.description).joined(separator: " ")
                    out.append(line(ns, "-setdnsservers", q, dns, code: 23))
                } else {
                    // No router: this service never offers a default route.
                    out.append(line(ns, "-setmanual", q, "\(e.ip)", "\(e.mask)", code: 22))
                }
                out.append(line(ns, "-setv6off", q, code: 24))
                out.append("echo " + ShellQuote.word("Added service \(name)"))
            }
        case .temporary:
            if p.gateway != nil {
                throw PlanError("Gateway support needs the Persistent mode. Switch modes or turn Gateway support off.")
            }
            var subnets: [CidrEntry] = []
            for e in p.entries {
                // A second address in a subnet that is already on the interface gets a /32 mask,
                // otherwise the kernel refuses the duplicate subnet route.
                let shared = subnets.contains { $0.network == e.network && $0.prefix == e.prefix }
                let mask = shared ? IPv4.mask(prefix: 32) : e.mask
                subnets.append(e)
                out.append(line(ifc, dev, "inet", "\(e.ip)", "netmask", "\(mask)", "alias", code: 30))
                out.append("echo " + ShellQuote.word("Added alias \(e.ip) netmask \(mask) on \(dev)"))
            }
        }
        return out.joined(separator: "\n")
    }

    /// The shell script for Return to DHCP. The chosen service is set back to DHCP only when it
    /// was found in Manual mode (DNS is cleared then too); otherwise it is left exactly as it is.
    public static func dhcpScript(service: NetworkService, ipbindServices: [String], aliases: [IPv4],
                                  serviceWasManual: Bool) throws -> String {
        try check(service)
        var out = ["PATH=/usr/bin:/bin:/usr/sbin:/sbin"]
        out += try removeServices(ipbindServices)
        out += removeAliases(aliases, device: service.device)
        if serviceWasManual {
            let q = ShellQuote.word(service.name)
            out.append(line(ns, "-setdhcp", q, code: 40))
            out.append(line(ns, "-setdnsservers", q, "Empty", code: 41))
        }
        out.append("echo done")
        return out.joined(separator: "\n")
    }
}

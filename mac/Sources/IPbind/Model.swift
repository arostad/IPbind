import AppKit
import Foundation
import IPbindCore
import SwiftUI

let appVersion = "0.1.0-mac-preview"

@MainActor
final class Model: ObservableObject {
    enum StatusKind { case neutral, ok, fail }

    @Published var services: [NetworkService] = []
    @Published var selectedName: String = "" { didSet { if selectedName != oldValue, !selectedName.isEmpty { d.set(selectedName, forKey: K.adapter); log("Interface set to '\(selectedName)' (remembered).") } } }
    @Published var showAll = false { didSet { refresh() } }
    @Published var ipText: String
    @Published var gatewayOn: Bool { didSet { if gatewayOn != oldValue { d.set(gatewayOn, forKey: K.gwOn); log(gatewayOn ? "Gateway support on (remembered). Choose one address and enter its gateway and optional DNS." : "Gateway support off (remembered). Static IPs only, no gateway or DNS.") } } }
    @Published var gwChoice: String { didSet { d.set(gwChoice, forKey: K.gwChoice) } }
    @Published var gateway: String { didSet { d.set(gateway, forKey: K.gw) } }
    @Published var dns1: String { didSet { d.set(dns1, forKey: K.dns1) } }
    @Published var dns2: String { didSet { d.set(dns2, forKey: K.dns2) } }
    @Published var mode: BindMode { didSet { d.set(mode.rawValue, forKey: K.mode) } }
    @Published var console = ""
    @Published var status = "Ready."
    @Published var statusKind = StatusKind.neutral
    @Published var busy = false
    @Published var showReminder: Bool

    private let d = UserDefaults.standard
    private enum K {
        static let list = "ipList", adapter = "adapter", gwOn = "gatewayOn", gwChoice = "gatewayChoice",
                   gw = "gateway", dns1 = "dns1", dns2 = "dns2", mode = "mode", reminder = "dhcpReminderDismissed",
                   aliases = "lastAliases"
    }

    init() {
        ipText = d.string(forKey: K.list) ?? AddressList.defaults.joined(separator: "\n")
        gatewayOn = d.bool(forKey: K.gwOn)
        gwChoice = d.string(forKey: K.gwChoice) ?? ""
        gateway = d.string(forKey: K.gw) ?? ""
        dns1 = d.string(forKey: K.dns1) ?? ""
        dns2 = d.string(forKey: K.dns2) ?? ""
        mode = BindMode(rawValue: d.string(forKey: K.mode) ?? "") ?? .persistent
        showReminder = !d.bool(forKey: K.reminder)
        log("IPbind for Mac \(appVersion) (preview). Your DHCP service stays on; static addresses are added alongside it.")
        refresh(initial: true)
    }

    var visibleServices: [NetworkService] { services.filter { showAll ? !$0.isIPbindService : $0.looksWired } }
    var selected: NetworkService? { services.first { $0.name == selectedName && !$0.isIPbindService } }
    var gatewayChoices: [String] { AddressList.parse(ipText).entries.filter { $0.prefix < 32 }.map(\.description) }
    var ipLabel: String {
        gatewayOn ? "IP addresses to bind - one per line, CIDR (e.g. 192.168.1.98/24):"
                  : "IP addresses to bind - one per line, CIDR (e.g. 192.168.1.98/24). No gateway or DNS:"
    }

    func log(_ msg: String, _ level: String = "INFO") {
        let f = DateFormatter(); f.dateFormat = "HH:mm:ss"
        console += "[\(f.string(from: Date()))] \(level)  \(msg)\n"
    }

    func dismissReminder(dontShowAgain: Bool) {
        if dontShowAgain { d.set(true, forKey: K.reminder); log("Return to DHCP reminder turned off (remembered).") }
        showReminder = false
    }

    func refresh(initial: Bool = false) {
        services = System.services()
        let vis = visibleServices
        if vis.isEmpty {
            log(showAll ? "No network services found." : "No wired LAN interfaces found. Tick 'Show all' to list every adapter.", "WARN")
        }
        let saved = d.string(forKey: K.adapter) ?? ""
        if !vis.contains(where: { $0.name == selectedName }) {
            let pick = vis.first { $0.name == saved } ?? vis.first
            let old = d.string(forKey: K.adapter)
            selectedName = pick?.name ?? ""
            if initial, let old { d.set(old, forKey: K.adapter) }
        }
        let ours = services.filter(\.isIPbindService)
        if initial, !ours.isEmpty {
            log("\(ours.count) IPbind address service(s) are still set up from last time. Click Return to DHCP when you're done.", "WARN")
        }
    }

    func saveList() {
        d.set(ipText, forKey: K.list)
        log("Address list saved.", "OK")
    }

    func restoreDefaults() {
        var lines = ipText.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        let have = Set(AddressList.parse(lines.joined(separator: "\n")).entries.map(\.description))
        var added = 0
        for x in AddressList.defaults where !have.contains(x) { lines.append(x); added += 1 }
        ipText = lines.joined(separator: "\n")
        d.set(ipText, forKey: K.list)
        log("Restored defaults (added \(added) missing; kept custom entries).", "OK")
    }

    func showCurrent() {
        guard let s = selected else { log("No interface selected.", "WARN"); return }
        log("Current IPv4 config for '\(s.name)' (\(s.device)):")
        for line in System.serviceInfo(s.name).split(separator: "\n") where !line.hasPrefix("IPv6") { log("  " + line) }
        let info = System.ifconfig(s.device).info
        log("  Link: \(info.status ?? "unknown")")
        for a in info.addresses { log("  inet \(a.ip) netmask \(a.mask)") }
        let ours = services.filter { $0.isIPbindService && $0.device == s.device }
        for o in ours { log("  IPbind service: \(o.name)\(o.enabled ? "" : " (disabled)")") }
    }

    private func lastAliases() -> [IPv4] { (d.stringArray(forKey: K.aliases) ?? []).compactMap { IPv4($0) } }

    // MARK: Apply

    func apply() {
        guard let s = selected else { log("No interface selected.", "WARN"); return }
        guard Validate.device(s.device) else { log("Adapter validation failed: '\(s.name)' has no usable interface name.", "ERR"); return }

        let parsed = AddressList.parse(ipText)
        for sk in parsed.skipped { log("Skipping invalid entry: '\(sk.line)' (\(sk.reason))", "WARN") }
        guard !parsed.entries.isEmpty else { log("No valid addresses to apply.", "WARN"); return }

        let plan: GatewayPlan?
        switch buildGatewayPlan(enabled: gatewayOn, choice: gwChoice, entries: parsed.entries, gateway: gateway, dns1: dns1, dns2: dns2) {
        case .failure(let e): log(e.message, "ERR"); log("Nothing was applied. Fix the gateway settings and try again.", "ERR"); setStatus(false, "Failed to apply static IPs to selected LAN interface"); return
        case .success(let p): plan = p
        }

        let now = System.ifconfig(s.device).info
        if now.status != "active" {
            let a = NSAlert()
            a.messageText = "Interface not connected"
            a.informativeText = "'\(s.name)' (\(s.device)) is currently '\(now.status ?? "unknown")', not connected. The addresses will only work once a cable is plugged in.\n\nApply anyway?"
            a.addButton(withTitle: "Apply anyway"); a.addButton(withTitle: "Cancel")
            if a.runModal() != .alertFirstButtonReturn { log("Apply cancelled - interface not connected.", "WARN"); return }
            log("Proceeding on a disconnected interface at user request.", "WARN")
        }

        // Clean slate: our old services on this device, and any address from the list (or our
        // last temporary set) that is on the interface now.
        let stale = services.filter { $0.isIPbindService && $0.device == s.device }.map(\.name)
        let wanted = Set(parsed.entries.map(\.ip)).union(lastAliases())
        let staleAliases = now.addresses.map(\.ip).filter { wanted.contains($0) && !stale.contains(Names.service(device: s.device, ip: $0)) }

        let script: String
        do {
            script = try Commands.applyScript(ApplyPlan(service: s, entries: parsed.entries, gateway: plan, mode: mode,
                                                        staleServices: stale, staleAliases: staleAliases))
        } catch let e as PlanError { log(e.message, "ERR"); setStatus(false, "Failed to apply static IPs to selected LAN interface"); return }
        catch { log("\(error)", "ERR"); return }

        log("Binding \(parsed.entries.count) address(es) to '\(s.name)' (\(s.device)), \(mode == .persistent ? "Persistent" : "Temporary") mode. macOS will ask for an administrator password.")
        if let plan { log("Gateway \(plan.gateway) on \(plan.entry)\(plan.dns.isEmpty ? "" : ", DNS " + plan.dns.map(\.description).joined(separator: ", ")).") }
        runPrivileged(script, busyText: "Applying static IPs to selected LAN interface...") { [self] r in
            if r.cancelled { log("Cancelled at the password prompt. Nothing was changed.", "WARN"); setStatus(nil, "Ready."); return }
            if !r.ok { log("Apply failed: " + r.text.trimmingCharacters(in: .whitespacesAndNewlines), "ERR"); setStatus(false, "Failed to apply static IPs to selected LAN interface"); refresh(); return }
            d.set(mode == .temporary ? parsed.entries.map(\.ip.description) : [], forKey: K.aliases)
            for line in r.text.split(whereSeparator: { $0 == "\r" || $0 == "\n" }) { log("  " + line) }
            refresh()
            verify(s, expected: parsed.entries.map(\.ip))
            if mode == .temporary {
                log("Temporary mode: these aliases disappear after unplugging the cable, a link change, sleep, or a restart. Apply again if that happens.", "WARN")
            } else {
                log("Persistent mode: macOS keeps these addresses through restarts and replugs until you click Return to DHCP.", "WARN")
            }
            if plan != nil {
                log("The gateway service sits last in the service order, so Wi-Fi or your DHCP network still wins the default route if it has one.")
            }
            setStatus(true, "Static IPs applied to selected LAN interface")
        }
    }

    private func verify(_ s: NetworkService, expected: [IPv4]) {
        // Persistent services can take a moment for configd to apply.
        var info = System.ifconfig(s.device).info
        for _ in 0..<12 where !Set(expected).isSubset(of: Set(info.addresses.map(\.ip))) {
            Thread.sleep(forTimeInterval: 0.25)
            info = System.ifconfig(s.device).info
        }
        log("Bind complete - verifying:", "OK")
        let have = Set(info.addresses.map(\.ip))
        for ip in expected {
            log("  \(ip)  \(have.contains(ip) ? "present" : "not on the interface yet")", have.contains(ip) ? "OK" : "WARN")
        }
        if info.status != "active" { log("Interface is not connected - addresses will activate once it comes up.", "WARN") }
    }

    // MARK: Return to DHCP

    func returnToDhcp() {
        guard let s = selected else { log("No interface selected.", "WARN"); return }
        let ours = services.filter { $0.isIPbindService && $0.device == s.device }.map(\.name)
        let now = System.ifconfig(s.device).info
        let listed = Set(AddressList.parse(ipText).entries.map(\.ip)).union(lastAliases())
        let ourServiceIPs = Set(ours.compactMap { IPv4(String($0.split(separator: " ").last ?? "")) })
        let aliases = now.addresses.map(\.ip).filter { listed.contains($0) && !ourServiceIPs.contains($0) }
        let wasManual = System.serviceInfo(s.name).contains("Manual Configuration")

        if ours.isEmpty && aliases.isEmpty && !wasManual {
            log("'\(s.name)' is already on DHCP with no IPbind addresses.", "OK")
            setStatus(true, "Selected LAN interface returned to DHCP")
            return
        }
        let script: String
        do { script = try Commands.dhcpScript(service: s, ipbindServices: ours, aliases: aliases, serviceWasManual: wasManual) }
        catch { log("\(error)", "ERR"); return }
        log("Removing \(ours.count) IPbind service(s) and \(aliases.count) alias(es) from '\(s.name)'\(wasManual ? ", and switching it from Manual back to DHCP" : ""). macOS will ask for an administrator password.")
        runPrivileged(script, busyText: "Returning selected LAN interface to DHCP...") { [self] r in
            if r.cancelled { log("Cancelled at the password prompt. Nothing was changed.", "WARN"); setStatus(nil, "Ready."); return }
            if !r.ok { log("Return to DHCP failed: " + r.text.trimmingCharacters(in: .whitespacesAndNewlines), "ERR"); setStatus(false, "Failed to return selected LAN interface to DHCP"); refresh(); return }
            d.set([String](), forKey: K.aliases)
            refresh()
            log("Back to DHCP - verifying:", "OK")
            let info = System.ifconfig(s.device).info
            let left = info.addresses.map(\.ip).filter { listed.contains($0) || ourServiceIPs.contains($0) }
            if left.isEmpty { log("  No IPbind addresses left on \(s.device).", "OK") }
            for ip in left { log("  \(ip) is still on \(s.device).", "WARN") }
            let dhcp = info.addresses.map(\.ip).filter { !listed.contains($0) && !ourServiceIPs.contains($0) }
            if let ip = dhcp.first { log("DHCP address: \(ip) on '\(s.name)'.", "OK") }
            else { log("No DHCP address seen yet on '\(s.name)' (lease may still be pending).") }
            setStatus(left.isEmpty, left.isEmpty ? "Selected LAN interface returned to DHCP" : "Failed to return selected LAN interface to DHCP")
        }
    }

    // MARK: helpers

    private func runPrivileged(_ script: String, busyText: String,
                               done: @escaping @MainActor ((ok: Bool, cancelled: Bool, text: String)) -> Void) {
        busy = true
        status = busyText; statusKind = .neutral
        Task.detached {
            let r = System.runPrivileged(script)
            await MainActor.run { self.busy = false; done(r) }
        }
    }

    private func setStatus(_ ok: Bool?, _ text: String) {
        status = text
        statusKind = ok == nil ? .neutral : (ok! ? .ok : .fail)
    }
}

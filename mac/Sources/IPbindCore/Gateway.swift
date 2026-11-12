import Foundation

public struct GatewayPlan: Equatable {
    public let entry: CidrEntry
    public let gateway: IPv4
    public let dns: [IPv4]
}

/// Mirrors TryBuildGatewayPlan in the Windows app: .success(nil) when Gateway support is off.
public func buildGatewayPlan(enabled: Bool, choice: String?, entries: [CidrEntry],
                             gateway gwText: String, dns1 d1Text: String, dns2 d2Text: String)
    -> Result<GatewayPlan?, PlanError>
{
    guard enabled else { return .success(nil) }
    guard let choice, !choice.isEmpty else {
        return .failure(PlanError("Gateway support is on, but no address is chosen in 'Use gateway on'. Pick one or turn Gateway support off."))
    }
    guard let entry = entries.first(where: { $0.description == choice }) else {
        return .failure(PlanError("Gateway address '\(choice)' is no longer in the IP list."))
    }
    if entry.prefix >= 32 {
        return .failure(PlanError("A /32 address has no subnet, so it cannot use a gateway. Use a shorter prefix such as /24."))
    }
    let g = gwText.trimmingCharacters(in: .whitespaces)
    if g.isEmpty {
        return .failure(PlanError("Gateway support is on, but no gateway is entered. Enter one or turn Gateway support off."))
    }
    guard let gw = IPv4(g), gw.isUsableHost else {
        return .failure(PlanError("Gateway '\(g)' is not a valid IPv4 address."))
    }
    if !entry.contains(gw) {
        return .failure(PlanError("Gateway \(gw) is not in the same subnet as \(choice) (\(entry.network)/\(entry.prefix))."))
    }
    if gw == entry.ip {
        return .failure(PlanError("Gateway \(gw) is the same as the address it is for. Use the router's address."))
    }
    if entry.prefix <= 30 && (gw == entry.network || gw == entry.broadcast) {
        return .failure(PlanError("Gateway \(gw) is the \(gw == entry.network ? "network" : "broadcast") address of \(entry.network)/\(entry.prefix), not a usable host."))
    }
    let s1 = d1Text.trimmingCharacters(in: .whitespaces), s2 = d2Text.trimmingCharacters(in: .whitespaces)
    if !s2.isEmpty && s1.isEmpty {
        return .failure(PlanError("DNS 2 is filled in but DNS 1 is empty. Put the first DNS server in DNS 1."))
    }
    var dns: [IPv4] = []
    if !s1.isEmpty {
        guard let d = IPv4(s1), d.isUsableHost else { return .failure(PlanError("DNS 1 '\(s1)' is not a valid IPv4 address.")) }
        dns.append(d)
    }
    if !s2.isEmpty {
        guard let d = IPv4(s2), d.isUsableHost else { return .failure(PlanError("DNS 2 '\(s2)' is not a valid IPv4 address.")) }
        if d == dns.first { return .failure(PlanError("DNS 2 is the same as DNS 1. Clear DNS 2 or enter a different server.")) }
        dns.append(d)
    }
    return .success(GatewayPlan(entry: entry, gateway: gw, dns: dns))
}

public struct PlanError: Error, Equatable, CustomStringConvertible {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var description: String { message }
}

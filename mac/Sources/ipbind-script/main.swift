import Foundation
import IPbindCore

// usage: ipbind-script apply <service> <persistent|temporary> <cidr>... [--gw <cidr> <gateway> [dns...]]
//        ipbind-script dhcp <service> <alias ip>...
func run(_ args: [String]) -> String {
    let p = Process(); p.executableURL = URL(fileURLWithPath: "/usr/sbin/networksetup"); p.arguments = args
    let o = Pipe(); p.standardOutput = o; try! p.run()
    let d = o.fileHandleForReading.readDataToEndOfFile(); p.waitUntilExit(); return String(decoding: d, as: UTF8.self)
}
var a = Array(CommandLine.arguments.dropFirst())
guard a.count >= 2 else { fputs("usage\n", stderr); exit(2) }
let all = Parse.serviceOrder(run(["-listnetworkserviceorder"]))
guard let svc = all.first(where: { $0.name == a[1] }) else { fputs("no such service\n", stderr); exit(2) }
let ours = all.filter { $0.isIPbindService && $0.device == svc.device }.map(\.name)
do {
    if a[0] == "apply" {
        var gw: GatewayPlan? = nil
        var rest = Array(a.dropFirst(3))
        if let i = rest.firstIndex(of: "--gw") {
            let g = Array(rest[(i + 1)...]); rest = Array(rest[..<i])
            let es = AddressList.parse(rest.joined(separator: "\n")).entries
            gw = try buildGatewayPlan(enabled: true, choice: g[0], entries: es, gateway: g[1],
                                      dns1: g.count > 2 ? g[2] : "", dns2: g.count > 3 ? g[3] : "").get()
        }
        let es = AddressList.parse(rest.joined(separator: "\n")).entries
        print(try Commands.applyScript(ApplyPlan(service: svc, entries: es, gateway: gw, mode: BindMode(rawValue: a[2])!,
                                                 staleServices: ours, staleAliases: [])))
    } else {
        print(try Commands.dhcpScript(service: svc, ipbindServices: ours, aliases: a.dropFirst(2).compactMap { IPv4($0) },
                                      serviceWasManual: false))
    }
} catch { fputs("\(error)\n", stderr); exit(1) }

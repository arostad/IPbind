import Foundation
import IPbindCore

/// Runs fixed system tools with argument arrays (no shell), and the one privileged step.
enum System {
    struct Output { let status: Int32; let out: String; let err: String }

    static func run(_ tool: String, _ args: [String]) -> Output {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        let o = Pipe(), e = Pipe()
        p.standardOutput = o
        p.standardError = e
        do { try p.run() } catch { return Output(status: -1, out: "", err: error.localizedDescription) }
        let od = o.fileHandleForReading.readDataToEndOfFile()
        let ed = e.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return Output(status: p.terminationStatus, out: String(decoding: od, as: UTF8.self),
                      err: String(decoding: ed, as: UTF8.self))
    }

    static func services() -> [NetworkService] {
        Parse.serviceOrder(run("/usr/sbin/networksetup", ["-listnetworkserviceorder"]).out)
    }

    static func ifconfig(_ device: String) -> (text: String, info: Parse.InterfaceInfo) {
        guard Validate.device(device) else { return ("", Parse.InterfaceInfo()) }
        let t = run("/sbin/ifconfig", [device]).out
        return (t, Parse.ifconfig(t))
    }

    static func serviceInfo(_ name: String) -> String {
        guard Validate.name(name) else { return "" }
        return run("/usr/sbin/networksetup", ["-getinfo", name]).out
    }

    /// Runs the script as root through AppleScript, so macOS shows its standard password prompt.
    /// Returns nil on success, "cancelled" if the user cancelled, or the error text.
    static func runPrivileged(_ script: String) -> (ok: Bool, cancelled: Bool, text: String) {
        let r = run("/usr/bin/osascript", ["-e", AppleScript.adminShell(script)])
        if r.status == 0 { return (true, false, r.out) }
        let cancelled = r.err.contains("-128") || r.err.lowercased().contains("user canceled")
        return (false, cancelled, r.err)
    }
}

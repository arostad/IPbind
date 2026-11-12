import XCTest
@testable import IPbindCore

final class CoreTests: XCTestCase {
    let svc = NetworkService(name: "USB 10/100/1000 LAN", hardwarePort: "USB 10/100/1000 LAN", device: "en5", enabled: true)

    func testIPv4Strict() {
        XCTAssertEqual(IPv4("10.10.10.98")?.description, "10.10.10.98")
        XCTAssertEqual(IPv4(" 192.168.1.1 ")?.description, "192.168.1.1")
        for bad in ["256.1.1.1", "1.2.3", "1.2.3.4.5", "1.2.3.-4", "1.2.3.4;reboot", "1.2.3.4 && x", "", "1..2.3", "0001.1.1.1", "１.2.3.4"] {
            XCTAssertNil(IPv4(bad), bad)
        }
    }

    func testCidr() {
        XCTAssertEqual(CidrEntry("10.10.10.98/24")?.mask.description, "255.255.255.0")
        XCTAssertEqual(CidrEntry("10.10.10.98")?.prefix, 24)
        XCTAssertEqual(CidrEntry("10.0.0.5 / 8")?.mask.description, "255.0.0.0")
        XCTAssertNil(CidrEntry("10.0.0.5/33"))
        XCTAssertNil(CidrEntry("10.0.0.5/24/1"))
        XCTAssertNil(CidrEntry("10.0.0.5/24'"))
        XCTAssertNotNil(CidrEntry("10.0.0.0/24")?.hostProblem)
        XCTAssertNotNil(CidrEntry("127.0.0.2/8")?.hostProblem)
        XCTAssertNil(CidrEntry("10.0.0.0/31")?.hostProblem)
    }

    func testListParse() {
        let p = AddressList.parse("10.10.10.98/24\n\nbogus\n10.10.10.98/16\r\n192.168.1.98/24")
        XCTAssertEqual(p.entries.map(\.description), ["10.10.10.98/24", "192.168.1.98/24"])
        XCTAssertEqual(p.skipped.count, 2)
        XCTAssertEqual(AddressList.parse(AddressList.defaults.joined(separator: "\n")).entries.count, 8)
    }

    func testGateway() {
        let es = AddressList.parse("192.168.1.98/24\n10.0.0.5/32").entries
        func run(_ c: String?, _ g: String, _ d1: String = "", _ d2: String = "") -> Result<GatewayPlan?, PlanError> {
            buildGatewayPlan(enabled: true, choice: c, entries: es, gateway: g, dns1: d1, dns2: d2)
        }
        XCTAssertEqual(try? buildGatewayPlan(enabled: false, choice: nil, entries: es, gateway: "", dns1: "", dns2: "").get(), .some(nil))
        XCTAssertEqual(try? run("192.168.1.98/24", "192.168.1.1", "1.1.1.1", "8.8.8.8").get()?.dns.count, 2)
        for (c, g, d1, d2) in [(nil, "192.168.1.1", "", ""), ("192.168.1.98/24", "", "", ""), ("192.168.1.98/24", "10.0.0.1", "", ""),
                               ("192.168.1.98/24", "192.168.1.98", "", ""), ("192.168.1.98/24", "192.168.1.255", "", ""),
                               ("10.0.0.5/32", "10.0.0.1", "", ""), ("192.168.1.98/24", "192.168.1.1", "", "8.8.8.8"),
                               ("192.168.1.98/24", "192.168.1.1", "8.8.8.8", "8.8.8.8"), ("192.168.1.98/24", "192.168.1.1", "x", "")] {
            if case .success = run(c, g, d1, d2) { XCTFail("\(String(describing: c)) \(g) \(d1) \(d2) should fail") }
        }
    }

    func testQuoting() {
        XCTAssertEqual(ShellQuote.word("a'b"), "'a'\\''b'")
        XCTAssertEqual(AppleScript.literal("say \"hi\" \\ \n"), "\"say \\\"hi\\\" \\\\ \\n\"")
        XCTAssertFalse(Validate.name("-help"))
        XCTAssertFalse(Validate.name("Eth\nernet"))
        XCTAssertTrue(Validate.name("USB 10/100/1000 LAN"))
        XCTAssertFalse(Validate.device("en5;id"))
        XCTAssertTrue(Validate.device("en12"))
    }

    func testPersistentScript() throws {
        let es = AddressList.parse("192.168.1.98/24\n10.10.10.98/24").entries
        let g = try buildGatewayPlan(enabled: true, choice: "192.168.1.98/24", entries: es, gateway: "192.168.1.1", dns1: "1.1.1.1", dns2: "").get()
        let s = try Commands.applyScript(ApplyPlan(service: svc, entries: es, gateway: g, mode: .persistent,
                                                   staleServices: ["IPbind en5 10.1.1.1"], staleAliases: []))
        XCTAssertTrue(s.contains("-removenetworkservice 'IPbind en5 10.1.1.1'"))
        XCTAssertTrue(s.contains("-createnetworkservice 'IPbind en5 192.168.1.98' 'USB 10/100/1000 LAN'"))
        XCTAssertTrue(s.contains("-setmanual 'IPbind en5 192.168.1.98' 192.168.1.98 255.255.255.0 192.168.1.1 ||"))
        XCTAssertTrue(s.contains("-setmanual 'IPbind en5 10.10.10.98' 10.10.10.98 255.255.255.0 ||"))
        XCTAssertTrue(s.contains("-setdnsservers 'IPbind en5 192.168.1.98' 1.1.1.1"))
        XCTAssertFalse(s.contains("-setdhcp"))
        XCTAssertFalse(s.contains("'USB 10/100/1000 LAN' 192"), "the user's own service is never changed")
    }

    func testTemporaryScript() throws {
        let es = AddressList.parse("192.168.1.98/24\n192.168.1.99/24").entries
        let s = try Commands.applyScript(ApplyPlan(service: svc, entries: es, gateway: nil, mode: .temporary, staleServices: [], staleAliases: [IPv4("10.9.9.9")!]))
        XCTAssertTrue(s.contains("/sbin/ifconfig en5 inet 10.9.9.9 -alias"))
        XCTAssertTrue(s.contains("/sbin/ifconfig en5 inet 192.168.1.98 netmask 255.255.255.0 alias"))
        XCTAssertTrue(s.contains("/sbin/ifconfig en5 inet 192.168.1.99 netmask 255.255.255.255 alias"))
    }

    func testRejectsHostileNames() {
        let evil = NetworkService(name: "x", hardwarePort: "p", device: "en0; rm -rf /", enabled: true)
        XCTAssertThrowsError(try Commands.applyScript(ApplyPlan(service: evil, entries: [CidrEntry("10.0.0.2/24")!], gateway: nil, mode: .temporary, staleServices: [], staleAliases: [])))
        XCTAssertThrowsError(try Commands.dhcpScript(service: svc, ipbindServices: ["Wi-Fi"], aliases: [], serviceWasManual: false))
        // A quote in a real service name stays inside one shell word.
        let odd = NetworkService(name: "Bob's LAN", hardwarePort: "Bob's LAN", device: "en7", enabled: true)
        let s = try! Commands.dhcpScript(service: odd, ipbindServices: [], aliases: [], serviceWasManual: true)
        XCTAssertTrue(s.contains("-setdhcp 'Bob'\\''s LAN'"))
    }

    func testParseServiceOrder() {
        let text = """
        An asterisk (*) denotes that a network service is disabled.
        (1) USB 10/100/1000 LAN
        (Hardware Port: USB 10/100/1000 LAN, Device: en5)

        (2) Wi-Fi
        (Hardware Port: Wi-Fi, Device: en0)

        (*) IPbind en5 10.10.10.98
        (Hardware Port: USB 10/100/1000 LAN, Device: en5)

        (3) Tailscale
        (Hardware Port: com.tailscale.ipn.macsys, Device: )
        """
        let s = Parse.serviceOrder(text)
        XCTAssertEqual(s.map(\.device), ["en5", "en0", "en5", ""])
        XCTAssertEqual(s.filter(\.looksWired).map(\.name), ["USB 10/100/1000 LAN"])
        XCTAssertFalse(s[2].enabled)
        XCTAssertTrue(s[2].isIPbindService)
    }

    func testParseIfconfig() {
        let t = "en5: flags=8863<UP> mtu 1500\n\tinet 10.10.10.98 netmask 0xffffff00 broadcast 10.10.10.255\n\tinet 192.168.1.98 netmask 0xffffffff broadcast 192.168.1.98\n\tstatus: active\n"
        let i = Parse.ifconfig(t)
        XCTAssertEqual(i.addresses.map(\.ip.description), ["10.10.10.98", "192.168.1.98"])
        XCTAssertEqual(i.addresses[0].mask.description, "255.255.255.0")
        XCTAssertEqual(i.status, "active")
    }
}

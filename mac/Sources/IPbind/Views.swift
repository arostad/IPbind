import AppKit
import IPbindCore
import SwiftUI

enum Palette {
    static let bg = Color(red: 32 / 255, green: 32 / 255, blue: 32 / 255)
    static let panel = Color(red: 50 / 255, green: 50 / 255, blue: 50 / 255)
    static let input = Color(red: 24 / 255, green: 24 / 255, blue: 24 / 255)
    static let consoleBg = Color(red: 18 / 255, green: 18 / 255, blue: 18 / 255)
    static let fg = Color(red: 230 / 255, green: 230 / 255, blue: 230 / 255)
    static let sub = Color(red: 150 / 255, green: 150 / 255, blue: 150 / 255)
    static let border = Color(red: 80 / 255, green: 80 / 255, blue: 80 / 255)
    static let green = Color(red: 46 / 255, green: 125 / 255, blue: 50 / 255)
    static let blue = Color(red: 25 / 255, green: 90 / 255, blue: 160 / 255)
    static let amber = Color(red: 242 / 255, green: 179 / 255, blue: 61 / 255)
    static let okText = Color(red: 90 / 255, green: 200 / 255, blue: 110 / 255)
    static let failText = Color(red: 240 / 255, green: 100 / 255, blue: 100 / 255)
}

struct FlatButton: ButtonStyle {
    var fill = Palette.panel
    var bold = false
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(bold ? .system(size: 14, weight: .bold) : .system(size: 12))
            .multilineTextAlignment(.center)
            .foregroundColor(.white)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(fill.opacity(configuration.isPressed ? 0.7 : 1))
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(fill == Palette.panel ? Palette.border : .clear))
            .clipShape(RoundedRectangle(cornerRadius: 4))
    }
}

struct IPField: View {
    @Binding var text: String
    var body: some View {
        TextField("", text: $text)
            .textFieldStyle(.plain)
            .font(.system(size: 12, design: .monospaced))
            .padding(.horizontal, 5).frame(height: 22)
            .background(Palette.input)
            .overlay(RoundedRectangle(cornerRadius: 3).stroke(Palette.border))
    }
}

struct ContentView: View {
    @EnvironmentObject var m: Model
    @State private var showAbout = false

    var body: some View {
        HStack(alignment: .top, spacing: 20) {
            left.frame(width: 520)
            right.frame(width: 520)
        }
        .padding(20)
        .background(Palette.bg)
        .foregroundColor(Palette.fg)
        .sheet(isPresented: $m.showReminder) { ReminderView().environmentObject(m) }
        .sheet(isPresented: $showAbout) { AboutView() }
    }

    var left: some View {
        VStack(alignment: .leading, spacing: 8) {
            VStack(spacing: 2) {
                Text("IPbind").font(.system(size: 20, weight: .bold))
                Text("Static IP Binder for easily accessing air-gapped equipment spread across multiple IP ranges")
                    .font(.system(size: 11).italic()).foregroundColor(Palette.sub).multilineTextAlignment(.center)
            }.frame(maxWidth: .infinity)

            Text("Select your LAN interface below:")
            HStack {
                Picker("", selection: $m.selectedName) {
                    ForEach(m.visibleServices) { s in
                        Text("\(s.name) | \(s.device.isEmpty ? "no device" : s.device)\(s.enabled ? "" : " [disabled]")").tag(s.name)
                    }
                }.labelsHidden()
                Button("Refresh") { m.refresh() }.buttonStyle(FlatButton()).frame(width: 92, height: 24)
            }
            HStack {
                Toggle("Show all adapters (include Wi-Fi / VPN / bridges)", isOn: $m.showAll).font(.system(size: 11))
                Spacer()
                Toggle("Gateway support", isOn: $m.gatewayOn).font(.system(size: 11))
                    .help("Only enable if you know what you're doing!\nA gateway or DNS here can change how this Mac reaches the internet.")
            }
            HStack {
                Text("Mode:").font(.system(size: 11))
                Picker("", selection: $m.mode) {
                    Text("Persistent (keeps through restarts)").tag(BindMode.persistent)
                    Text("Temporary (lost on unplug)").tag(BindMode.temporary)
                }.labelsHidden().pickerStyle(.segmented)
            }
            .help("Persistent adds one extra network service per address on this adapter. Temporary adds ifconfig aliases that vanish when the link drops. Your DHCP service stays on either way.")

            Text(m.ipLabel).font(.system(size: 11))
            TextEditor(text: $m.ipText)
                .font(.system(size: 13, design: .monospaced))
                .scrollContentBackground(.hidden)
                .background(Palette.input)
                .overlay(RoundedRectangle(cornerRadius: 3).stroke(Palette.border))
                .frame(maxHeight: .infinity)

            if m.gatewayOn { gatewayRows }

            HStack(spacing: 11) {
                Button("Save List") { m.saveList() }.buttonStyle(FlatButton())
                Button("Restore Defaults") { m.restoreDefaults() }.buttonStyle(FlatButton())
                Button("Show Current") { m.showCurrent() }.buttonStyle(FlatButton())
            }.frame(height: 28)

            HStack(spacing: 10) {
                Button("Apply Static IPs to\nLAN Interface") { m.apply() }.buttonStyle(FlatButton(fill: Palette.green, bold: true))
                Button("Return LAN Interface\nto DHCP") { m.returnToDhcp() }.buttonStyle(FlatButton(fill: Palette.blue, bold: true))
            }.frame(height: 60).disabled(m.busy)

            Text(m.status)
                .font(.system(size: 13, weight: .bold))
                .foregroundColor(m.statusKind == .ok ? Palette.okText : m.statusKind == .fail ? Palette.failText : Color(white: 0.78))
                .frame(maxWidth: .infinity, minHeight: 38)
        }
    }

    var gatewayRows: some View {
        Grid(alignment: .leading, horizontalSpacing: 8, verticalSpacing: 6) {
            GridRow {
                Text("Use gateway on:")
                Picker("", selection: $m.gwChoice) {
                    Text("Choose…").tag("")
                    ForEach(m.gatewayChoices, id: \.self) { Text($0).tag($0) }
                }.labelsHidden().frame(width: 160)
                Text("Gateway:")
                IPField(text: $m.gateway)
            }
            GridRow {
                Text("DNS 1:")
                IPField(text: $m.dns1).frame(width: 160)
                Text("DNS 2:")
                IPField(text: $m.dns2)
            }
        }.font(.system(size: 11))
    }

    var right: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Console output:")
                Spacer()
                Button("Clear") { m.console = "" }.buttonStyle(FlatButton()).frame(width: 80, height: 24)
            }
            ScrollViewReader { proxy in
                ScrollView {
                    Text(m.console)
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundColor(Color(white: 0.82))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(6)
                    Color.clear.frame(height: 1).id("end")
                }
                .background(Palette.consoleBg)
                .overlay(RoundedRectangle(cornerRadius: 3).stroke(Palette.border))
                .onChange(of: m.console) { _ in proxy.scrollTo("end", anchor: .bottom) }
            }
            HStack {
                Button("About") { showAbout = true }.buttonStyle(FlatButton()).frame(width: 96, height: 26)
                Spacer()
                Text("v" + appVersion).font(.system(size: 11)).foregroundColor(Color(white: 0.47))
            }
        }
    }
}

struct ReminderView: View {
    @EnvironmentObject var m: Model
    @State private var understand = false
    @State private var dontShow = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                Image(systemName: "exclamationmark.triangle.fill").font(.system(size: 26)).foregroundColor(Palette.amber)
                (Text("Important:").bold().foregroundColor(Palette.amber) + Text(" Return to DHCP when you\u{2019}re done"))
                    .font(.system(size: 17))
            }
            Text("IPbind gives your Ethernet adapter static IP addresses so you can reach field equipment. They stay in place until you click Return to DHCP.")
            Text("If you plug this adapter into an office, public, or home network while those addresses are still set:")
            VStack(alignment: .leading, spacing: 4) {
                ForEach([
                    "One of your addresses may already belong to a device on that network, and you could knock it offline",
                    "Your addresses may fall inside that network\u{2019}s DHCP pool and clash with addresses it hands out",
                    "You likely won\u{2019}t have internet or network access",
                    "You could trip network security alarms, which may get your laptop blocked by network admins",
                ], id: \.self) { item in
                    HStack(alignment: .firstTextBaseline, spacing: 8) { Text("\u{2022}"); Text(item) }
                }
            }.padding(.leading, 4)
            (Text("Click ") + Text("Return to DHCP").bold() + Text(" before plugging this adapter into a non-air-gapped network."))
                .padding(.top, 4)
            HStack(alignment: .bottom) {
                VStack(alignment: .leading, spacing: 6) {
                    Toggle("I understand", isOn: $understand)
                    Toggle("Don\u{2019}t show this again", isOn: $dontShow)
                }
                Spacer()
                Button("Exit") { NSApp.terminate(nil) }.buttonStyle(FlatButton()).frame(width: 110, height: 32)
                Button("Continue") { m.dismissReminder(dontShowAgain: dontShow) }
                    .buttonStyle(FlatButton()).frame(width: 110, height: 32)
                    .disabled(!understand).opacity(understand ? 1 : 0.45)
            }.padding(.top, 6)
        }
        .fixedSize(horizontal: false, vertical: true)
        .padding(24)
        .frame(width: 560)
        .background(Palette.bg)
        .foregroundColor(Palette.fg)
        .interactiveDismissDisabled()
    }
}

struct AboutView: View {
    @Environment(\.dismiss) var dismiss
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 48, height: 48)
                VStack(alignment: .leading) {
                    Text("IPbind").font(.system(size: 22, weight: .bold))
                    Text("Version \(appVersion)").font(.system(size: 12))
                }
            }
            Text("One-click Static IP Binder for easily accessing\nair-gapped equipment spread across multiple IP ranges")
                .font(.system(size: 12)).padding(.vertical, 6)
            HStack(spacing: 0) {
                Text("App created by ")
                Link("Andy Rostad", destination: URL(string: "https://github.com/arostad")!)
            }
            HStack(spacing: 0) {
                Text("Released under the ")
                Link("MIT License", destination: URL(string: "https://github.com/arostad/IPbind/blob/main/LICENSE")!)
            }
            Text("Mac preview: no update checks.").font(.system(size: 11)).foregroundColor(Palette.sub)
            HStack { Spacer(); Button("Close") { dismiss() }.buttonStyle(FlatButton()).frame(width: 96, height: 28) }
        }
        .padding(24)
        .frame(width: 430)
        .background(Palette.bg)
        .foregroundColor(Palette.fg)
    }
}

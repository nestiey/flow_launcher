import AppKit

final class Launcher: NSObject, NSApplicationDelegate {
    private var window: NSWindow!
    private let play = NSButton(title: "▶ 플레이", target: nil, action: nil)
    private let status = NSTextField(wrappingLabelWithString: "")
    private var game: Process?

    func applicationDidFinishLaunching(_ notification: Notification) {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 820, height: 540),
                          styleMask: [.titled, .closable, .miniaturizable],
                          backing: .buffered, defer: false)
        window.title = "FLOW Launcher"
        let base = NSView(frame: window.contentView!.bounds)
        window.contentView = base
        base.wantsLayer = true
        base.layer?.backgroundColor = NSColor(calibratedRed: 0.92, green: 0.96, blue: 1, alpha: 1).cgColor
        if let url = Bundle.main.url(forResource: "Hero", withExtension: "png"),
           let image = NSImage(contentsOf: url) {
            let hero = NSImageView(frame: NSRect(x: 370, y: 0, width: 450, height: 540))
            hero.image = image
            hero.imageScaling = .scaleProportionallyUpOrDown
            base.addSubview(hero)
        }

        let heading = NSTextField(labelWithString: "Sunlit Valley")
        heading.font = .systemFont(ofSize: 28, weight: .bold)
        heading.frame = NSRect(x: 32, y: 440, width: 320, height: 44)
        base.addSubview(heading)
        status.font = .systemFont(ofSize: 14)
        status.frame = NSRect(x: 32, y: 142, width: 315, height: 255)
        base.addSubview(status)
        play.frame = NSRect(x: 32, y: 57, width: 315, height: 52)
        play.bezelStyle = .rounded
        play.keyEquivalent = "\r"
        play.target = self
        play.action = #selector(start)
        base.addSubview(play)
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc private func start() {
        guard game == nil else { return }
        #if arch(arm64)
        let architecture = "arm64"
        #else
        let architecture = "x64"
        #endif
        guard let resources = Bundle.main.resourceURL else { return }
        let process = Process()
        process.executableURL = resources.appendingPathComponent("osx-\(architecture)/flow-mac")
        let output = Pipe()
        process.standardOutput = output
        process.standardError = output
        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            if data.isEmpty { handle.readabilityHandler = nil; return }
            let message = String(decoding: data, as: UTF8.self)
            DispatchQueue.main.async {
                self?.status.stringValue = String(message.suffix(650))
            }
        }
        process.terminationHandler = { [weak self] child in
            DispatchQueue.main.async {
                guard let self else { return }
                self.game = nil
                self.play.isEnabled = true
                if child.terminationStatus != 0 {
                    self.status.stringValue += "\n종료 코드 \(child.terminationStatus)"
                }
            }
        }
        do {
            try process.run()
            game = process
            play.isEnabled = false
            status.stringValue = "Sunlit Valley 준비 중…"
        } catch {
            output.fileHandleForReading.readabilityHandler = nil
            status.stringValue = error.localizedDescription
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

let app = NSApplication.shared
let delegate = Launcher()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()

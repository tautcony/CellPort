# CellPort

**[简体中文](README.md) | English**

CellPort is an Avalonia desktop manager for DJI first-generation 4G modules on Windows and macOS. It provides SMS, call controls, device status, eSIM / eUICC profile management, an AT console, and related settings.

![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS-blue)
![.NET](https://img.shields.io/badge/.NET-10.0%20Avalonia-512BD4)
![Release](https://img.shields.io/github/v/release/xmgzxmgz/CellPort)
![License](https://img.shields.io/badge/license-MIT-green)

## Features

- **Device status**: connection management; module, firmware, SIM, carrier, network, signal, and interface information.
- **SMS**: send and receive messages, reassemble long messages, manage conversations, export CSV / JSON, and optionally forward messages.
- **Calls**: dial, answer, hang up, use DTMF and mute, and view call history.
- **eSIM / eUICC**: read EID and profile information; enable, disable, and delete profiles; edit profile nicknames.
- **USSD and phonebook**: query USSD services and read SIM contacts.
- **Settings**: theme, runtime logs, update checks, and SMS forwarding services.
- **AT console**: execute AT commands and view command history.

## Hardware and platforms

CellPort targets DJI first-generation 4G modules based on the Quectel EG25-G, including the Baiwang QDC507. Known USB identifiers include `2CA3:4006` (QDC507) and `2C7C:0125` (Quectel EG25-G).

| Platform | Connection methods |
|---|---|
| Windows | WinUSB, system serial ports |
| macOS | Direct USB access through libusb, system serial fallback |

## Download

Download the Windows x64 release from [GitHub Releases](https://github.com/xmgzxmgz/CellPort/releases/latest):

- Framework-dependent ZIP: requires the .NET 10 Runtime.
- Self-contained ZIP: includes the .NET Runtime.
- Windows installer.

## Build and run

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

### Windows

```powershell
git clone https://github.com/xmgzxmgz/CellPort.git
cd CellPort
dotnet build CellPort.slnx -c Release
dotnet run --project src/CellPort.App -f net10.0-windows -- --page device
```

Windows USB direct access uses WinUSB; serial access uses the system serial ports provided by the Quectel driver.

### macOS

```bash
./scripts/run-mac.sh --page device
dotnet build CellPort.slnx -c Release -p:EnableWindowsTargeting=true
```

To create a self-contained `.app` for the host architecture:

```bash
brew install libusb
./scripts/publish-mac.sh
```

The output is placed in `dist/osx-arm64/CellPort.app` or `dist/osx-x64/CellPort.app`, depending on the target architecture.

To select the startup page:

```text
CellPort.exe --page <sms|calls|device|esim|console|settings>
```

## Documentation

- [macOS run guide](docs/macos.md)
- [Third-party licenses](THIRD-PARTY-NOTICES.md)

## License

[MIT](LICENSE)

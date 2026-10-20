# CellPort

**简体中文 | [English](README.en.md)**

CellPort 是面向大疆一代 4G 模块的 Avalonia 桌面管理应用，适用于 Windows 和 macOS。应用提供短信、通话控制、设备状态、eSIM / eUICC Profile 管理、AT 控制台及相关设置。

![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS-blue)
![.NET](https://img.shields.io/badge/.NET-10.0%20Avalonia-512BD4)
![Release](https://img.shields.io/github/v/release/xmgzxmgz/CellPort)
![License](https://img.shields.io/badge/license-MIT-green)

## 功能

- **设备状态**：连接管理；显示模块、固件、SIM、运营商、网络、信号及接口信息。
- **短信**：收发短信、长短信拼接、会话管理和 CSV / JSON 导出；可选短信转发。
- **通话**：拨号、接听、挂断、DTMF、静音和通话记录。
- **eSIM / eUICC**：读取 EID 和 Profile 信息；启用、停用、删除 Profile 和修改昵称。
- **USSD 与电话簿**：查询 USSD 服务并读取 SIM 联系人。
- **设置**：主题、运行日志、更新检查和短信转发服务配置。
- **AT 控制台**：执行 AT 指令并查看指令历史。

## 硬件与平台

CellPort 面向基于 Quectel EG25-G 的大疆一代 4G 模块，适用型号包括 Baiwang QDC507。已知 USB 标识包括 `2CA3:4006`（QDC507）和 `2C7C:0125`（Quectel EG25-G）。

| 平台 | 连接方式 |
|---|---|
| Windows | WinUSB、系统串口 |
| macOS | libusb USB 直连、系统串口回退 |

## 下载

前往 [GitHub Releases](https://github.com/xmgzxmgz/CellPort/releases/latest) 获取 Windows x64 版本：

- Framework-dependent ZIP：需要安装 .NET 10 Runtime。
- Self-contained ZIP：包含 .NET Runtime。
- Windows 安装程序。

## 构建与运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

### Windows

```powershell
git clone https://github.com/xmgzxmgz/CellPort.git
cd CellPort
dotnet build CellPort.slnx -c Release
dotnet run --project src/CellPort.App -f net10.0-windows -- --page device
```

Windows USB 直连使用 WinUSB；串口连接使用 Quectel 驱动提供的系统串口。

### macOS

```bash
./scripts/run-mac.sh --page device
dotnet build CellPort.slnx -c Release -p:EnableWindowsTargeting=true
```

创建本机架构的 self-contained `.app`：

```bash
brew install libusb
./scripts/publish-mac.sh
```

产物位于 `dist/osx-arm64/CellPort.app` 或 `dist/osx-x64/CellPort.app`，具体取决于目标架构。

指定应用启动页面：

```text
CellPort.exe --page <sms|calls|device|esim|console|settings>
```

## 文档

- [macOS 运行说明](docs/macos.md)
- [第三方依赖许可](THIRD-PARTY-NOTICES.md)

## License

[MIT](LICENSE)

# macOS 运行说明

`CellPort.App` 是 Windows 与 macOS 共用的 canonical Avalonia 应用。六个管理页面都已从 WPF 迁移；macOS 先按 VID/PID 与 IOKit 设备位置匹配模块，通过 libusb 接口 2 访问 AT 通道，接口 3 为备用；不可用时回退到 `/dev/cu.*` 串口。通话音频目前只有控制能力，页面会明确显示媒体路径不可用。

## 运行

源码运行需要 .NET 10 SDK。在项目根目录执行：

```bash
./scripts/run-mac.sh
# 直接进入控制台或设置页
./scripts/run-mac.sh --page console
./scripts/run-mac.sh --page settings
```

以上脚本调用 `src/CellPort.App`。统一的 `CellPort.slnx` 包含 Windows target 项目；在 macOS 上日常开发可用 `CellPort.Mac.slnf`，需要构建完整 solution 时传入 `EnableWindowsTargeting`。验证入口：

```bash
dotnet build CellPort.Mac.slnf -c Release
dotnet test CellPort.Mac.slnf --no-restore -v:minimal
dotnet build CellPort.slnx -c Release -p:EnableWindowsTargeting=true
```

## 当前功能范围

| 功能 | 状态 |
| --- | --- |
| 主窗口、中文、页面切换、六个完整页面 | Avalonia 实现；Headless UI 测试 25/25 通过；self-contained `osx-arm64` `.app` 已启动并自动连接模块 |
| 无设备启动、连接、失败提示 | 断开状态可启动；已接入模块识别状态与精简失败提示 |
| USB AT 连接 | 已在 QDC507 (`2CA3:4006`) 上通过 libusb 接口 2 完成连接和 AT 探测 |
| `/dev/cu.*` 串口、手动选择、重新连接、端口记忆 | USB 直连不可用时的回退路径；由 UI/平台测试覆盖 |
| 短信会话、发送/刷新、导出、验证码复制 | 已迁移；真机收发与文件选择器仍待实机确认 |
| 通话拨号、接听、挂断、DTMF、静音、来电/隐私和历史 | 已迁移；UI fake 通道可测服务接线，真机语音控制待验证 |
| 设备状态与平台诊断 | QDC507 型号、SIM 状态、运营商、网络与信号已从真实设备读取；诊断显示 `QDC507 · 2CA3:4006` |
| USSD、电话簿及复制 | 已迁移；假通道测试通过，真机查询和 macOS 剪贴板尚未验证 |
| eSIM 读取、切换、昵称、删除、重启重连 | 已迁移；需测试卡和真机验证写操作 |
| 控制台、设置、隐私、转发、更新检查 | 已迁移；网络更新与 webhook 投递需分别验证 |
| USB 直连 | macOS 使用 libusb；Windows 使用 WinUSB 绑定和平台 API |
| 通话媒体音频 | 未新增媒体后端；当前真实状态为仅通话控制 |

接入已知模块后，应用先按 VID/PID 和 IOKit `locationID` 匹配 USB 设备，再打开 AT 接口。QDC507 的主 AT 接口为 2，备用接口为 3；传输使用 bulk IN/OUT。若 USB 直连不可用，应用会探测 `/dev/cu.*` callout 节点，并过滤蓝牙与系统调试端口。

USB 与串口通道由 `CellPort.Platform.MacOS` 实现 `IAtChannelProvider`，并由 canonical app 的平台服务组合选择；AT framing 和 URC 处理复用 `CellPort.Core`。`.app` 会打包当前架构的 `libusb-1.0.dylib`。桌面使用
[Avalonia 的桌面生命周期](https://docs.avaloniaui.net/docs/fundamentals/application-lifetimes)。

## 打包和双击启动

在 Mac 上执行：

```bash
# 默认采用本机架构，也可明确指定 osx-arm64 或 osx-x64
./scripts/publish-mac.sh
open dist/osx-arm64/CellPort.app
```

输出包括 .NET 运行时、原生库、图标、Info.plist 和许可文件。
`.app` 使用本地 ad-hoc 签名，适合本机构建运行；当前没有 Developer ID 签名或公证。
打包结构参考 [Avalonia macOS 部署文档](https://docs.avaloniaui.net/docs/deployment/macos)。
App bundle 的可执行文件身份为 `CellPort`。本次最终 bundle 验证针对 `osx-arm64`；Intel `osx-x64` 发布尚未验证。

最终 `osx-arm64` self-contained `.app` 已通过代码签名与 plist 检查，并在 QDC507 上完成接口 2 claim、AT 探测和设备/SIM/网络状态读取。Intel (`osx-x64`) bundle、短信收发、USSD、电话簿、eSIM 写操作与通话仍待验证。

## 最终迁移验收记录

- `src/CellPort.App` Release 构建：0 警告、0 错误。
- 迁移验收时的 `CellPort.slnx` Release 构建：0 警告、0 错误；当时 Windows 工具和诊断位于独立 solution，在 macOS 上加 `-p:EnableWindowsTargeting=true` 跨目标构建同样为 0 警告、0 错误。当前已合并为单一 `CellPort.slnx`，macOS 过滤视图仍由 `CellPort.Mac.slnf` 提供。
- Windows x64 framework-dependent 与 self-contained 发布均成功，产物包含 `CellPort.exe` 及 Core、Audio、Platform.Windows、Transport.Serial 程序集。
- 自动化测试：Core 89/89、Audio.Core 39/39、App 25/25、macOS 18/18、Linux 平台 6/6，共 177 项通过。
- Windows 工具和诊断在 macOS 交叉构建通过，0 警告、0 错误。
- 最终 macOS `osx-arm64` `.app` 已检查签名、plist 和 libusb linkage，并实际连接 QDC507 读取状态。

真实设备 AT、SIM 就绪、运营商、网络注册、制式与信号读取已验证；短信、USSD、电话簿、eSIM 写操作、Windows 上的实际运行和驱动行为、媒体/音频后端仍未验证。通话音频能力保持 control-only；本次迁移没有新增媒体后端。

## 配置与排查

- 设置：`~/Library/Application Support/CellPort/settings.json`
- 日志：`~/Library/Application Support/CellPort/logs/cellport-yyyyMMdd.log`
- 窗口“设置与诊断”页显示本次端口发现与连接日志；未处理异常写入文件日志。
- Windows 与 macOS 共用原 `settings.json` 字段与格式；串口偏好和主题等数据保存在同一配置中。
- 若端口存在但无法打开，检查端口是否被串口调试工具或其他模块管理程序占用。

## 首阶段 macOS 宿主的历史验证（2026-10-20）

以下记录验证的是最初只有设备/控制台/设置功能的临时 Avalonia Desktop 宿主。它是早期历史记录，不是当前六页 canonical app 迁移的验收结果；当前验收状态见上文和 [Avalonia 迁移矩阵](avalonia-migration.md)。

2026-10-20，在 Apple Silicon macOS、.NET SDK 10.0.302 下：

- Release 构建成功，零警告、零错误。
- macOS 串口适配测试 6/6 通过，Core 测试 89/89 通过。
- 实际启动源码入口及 self-contained `.app`，检查主窗口、控制台和设置诊断页面。
- 无设备时自动连接失败后界面仍正常响应。
- `.app` 的 `codesign --verify --deep --strict` 验证通过。
- 当前没有真实模块接入，尚未验证硬件收发、短信、eSIM 操作或通话音频。

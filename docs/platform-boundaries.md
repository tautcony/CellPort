# 平台边界与兼容回归

`isolate-platform-dependencies` 只隔离实现，不增加音频或 Linux/macOS 设备支持。其后续音频工作将 Windows App 和公共契约基线升级到 .NET 10；此处记录当前项目边界。

| 项目 | 目标 | 职责 |
| --- | --- | --- |
| CellPort.Core | net10.0 | AT、短信、通话、eSIM、业务模型和 IAtChannel/IAtChannelProvider |
| CellPort.Audio.Core | net10.0 | 跨平台音频会话、设备、运行时、媒体契约与不可变结果模型 |
| CellPort.Transport.Serial | net10.0 | System.IO.Ports 传输及串口枚举，不引用 Windows 项目 |
| CellPort.Platform.Windows | net10.0-windows | WinUSB、SetupAPI、WMI/PnP、Windows 串口发现策略 |
| CellPort.App / 现有工具 | net10.0-windows | Windows UI/入口装配；PortProbe 仍为独立 Windows 诊断工具 |

宿主使用 `new ModemManager(new WindowsAtChannelProvider())`。Core 不提供默认平台实例，也不通过静态注册/反射寻找实现。外部源码调用方需更新构造函数和迁移类型的命名空间；这不保持旧程序集二进制 API 兼容。

`IAtChannelProvider` 将 USB 尝试、串口发现和创建隔离，现有 `IAtChannel` 继续承载传输。端口路径由提供者原样返回，比较规则由其 `PortNameComparer` 给出。Windows 使用 OrdinalIgnoreCase；Unix 宿主应按真实标识语义选择比较器。ModuleScanner/ModuleDiscoveryResult 是 Windows 诊断 API，WPF 可直接引用，不应作为通用强身份契约。

Windows 连接仍依次尝试 USB、指定串口或历史端口、其余端口并行探测；保留原蓝牙过滤、缓存、超时、端口记忆和初始化命令。原有扫描去重/StableKey、超时后后台打开任务等局限未在本次改写。后续音频身份绑定不得依赖旧 StableKey。

## 离线验证

在任一 OS 运行以下入口验证公共代码，不需要构建整个 Windows solution：

```text
dotnet test tests/CellPort.Core.Tests/CellPort.Core.Tests.csproj -c Release
dotnet test tests/CellPort.Audio.Core.Tests/CellPort.Audio.Core.Tests.csproj -c Release
dotnet build src/CellPort.Audio.Core/CellPort.Audio.Core.csproj -c Release
dotnet build src/CellPort.Transport.Serial/CellPort.Transport.Serial.csproj -c Release
```

Windows 另外执行 solution build 与两种 publish，见 `.github/workflows/platform-checks.yml`。模拟通道测试验证连接顺序、失败释放、初始化命令、状态/连接事件、端口记忆以及大小写策略；原短信编码/导出、eSIM 编码、更新检测测试继续运行。架构测试禁止 Core 引用平台实现、System.Management/System.IO.Ports/WPF 或包含 P/Invoke。

## 前置 change 的历史验证记录

验证日期：2026-10-19；环境：Windows x64、.NET 9 SDK。本节记录前置平台隔离 change 当时的结果。

- 原始基线：在隔离的 `HEAD` 快照 `f8c5d158b1a7f0870dd7a08a3bcba50b06a74bef` 上运行 Core 测试，30/30 通过，0 失败、0 跳过。
- 当前代码：`dotnet test tests/CellPort.Core.Tests/CellPort.Core.Tests.csproj -c Release --nologo`，40/40 通过；其中包含模拟通道回归和 Core 平台依赖检查。
- 当前代码：`dotnet build src/CellPort.Transport.Serial/CellPort.Transport.Serial.csproj -c Release --nologo` 成功；`dotnet build CellPort.sln -c Release --nologo` 成功，0 警告、0 错误。
- 迁移核对：SerialAtChannel、UsbAtChannel、ModuleScanner 与基线实现比较后仅有命名空间/导入调整和 Serial 文档描述更新；自动串口探测与端口探测代码在方法名归一化后逐行一致。
- framework-dependent 和 self-contained 的 `win-x64` 发布均成功；两种输出都包含 `CellPort.Core.dll`、`CellPort.Platform.Windows.dll` 和 `CellPort.Transport.Serial.dll`。
- `isolate-platform-dependencies` 与 `add-cross-platform-call-audio` 均通过严格 OpenSpec 校验。
- 未执行：Linux/macOS CI、真机连接和 UI 人工冒烟；这些仍按上面的发布前清单验证，当前结果不表示已验证其他操作系统硬件支持。

## 音频 change 的本机验证记录

验证日期：2026-10-20；环境：Windows x64、.NET 10 SDK 10.0.401。

- Core 测试：40/40 通过；Audio.Core 纯逻辑测试：4/4 通过。
- `dotnet build CellPort.sln -c Release --nologo` 成功，0 警告、0 错误。
- framework-dependent 与 self-contained 的 win-x64 发布均成功；两种输出都含 CellPort.Audio.Core.dll、CellPort.Core.dll、CellPort.Platform.Windows.dll 和 CellPort.Transport.Serial.dll。
- Audio.Core 与测试项目均使用 net10.0。架构测试确认 Audio.Core 不引用 WPF、System.Management 或 System.IO.Ports。
- Audio.Core 纯逻辑测试项目在 Windows 主机分别以 linux-x64 和 osx-x64 RID 成功构建。
- Linux/macOS 的 matrix job 已配置，但本记录尚无这两种操作系统的 CI 结果。Inno Setup 编译器在本机不可用，因此安装脚本仅完成源码更新与检查，未编译安装器。

## .NET 10 音频 change 更新

Audio.Core 与纯逻辑测试以 net10.0 为目标，不引用 WPF、System.Management 或 System.IO.Ports。Windows App 继续以 net10.0-windows/WPF 构建，并引用 Audio.Core 与 Platform.Windows。可移植 CI 矩阵在 Windows、Ubuntu 和 macOS 上运行 Core/Audio.Core 测试并构建 Audio.Core；Windows job 另构建 solution，并发布 framework-dependent 与 self-contained win-x64 包。

## 发布前人工冒烟

以下为独立真机验收，离线测试通过不代表这些已完成：

1. 无设备启动、自动连接失败和设备诊断页面正常，驱动引导可打开。
2. 在允许连接的测试模块上验证驱动串口自动/指定连接、历史端口恢复、断开重连；具备 WinUSB 驱动的独立环境验证 USB 优先路径。
3. 校对设备信息、短信列表、呼叫控制 UI、eSIM 查询和日志/设置。发送短信、拨号、eSIM 写入仅在专用测试卡及明确授权下执行。
4. 分别启动 framework-dependent 和 self-contained 包，确认两个新程序集随包携带；不改变原用户配置。

不要将 E2E/EsimProbe 当成只读测试自动运行；ModemManager 初始化本身也会写 AT 设置。当前连接设备保持原状态。

## 后续变更

音频 change 依赖这里的平台边界，单独迁移 .NET 10、增加 Audio.Core 和各平台设备/媒体适配。非 Windows CI 验证只覆盖构建和离线逻辑；真实串口、USB、音频及权限能力需独立验证。回滚本重构时整体撤销项目拆分和宿主注入，无数据迁移。

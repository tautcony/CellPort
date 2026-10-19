# Proposal

## Why

当前 Core 同时包含业务协议和 Windows WMI、SetupAPI、WinUSB，并由 ModemManager 直接构造具体通道。音频跨平台实现前需要一个可独立回归的平台隔离前置变更，避免把平台迁移与音频行为变更混在一起。

## What Changes

- Core 改为平台中立目标，通过注入的通道提供者发现和创建通道。
- Windows 原生实现、PnP 扫描和现有串口自动探测策略迁入 Platform.Windows；可移植的 SerialPort 传输独立为 Transport.Serial。
- Windows App 和诊断工具在入口显式装配实现，保留 USB 优先、指定端口、历史端口、并行探测顺序及缓存、超时、设置和事件行为。
- 添加无需设备的回归测试与三平台 Core 测试 CI；验证 Windows 构建及两种发布产物。
- **BREAKING（内部源代码 API）**：ModemManager 构造函数要求通道提供者；迁移的具体类型改用对应平台/传输命名空间。仓库内调用方一并更新，最终用户功能和配置格式不变。

## Capabilities

### New Capabilities

无。纯架构重构，设置 `skip_specs: true`。

### Modified Capabilities

无。现有产品行为保持不变。

## Impact

涉及 Core、App、诊断工具、solution、测试和 CI。保留 .NET 9 运行时及安装器要求；.NET 10 升级仍归音频 change 独立处理。本 change 是 `add-cross-platform-call-audio` 平台边界工作的前置依赖，不提供 Linux/macOS 硬件支持、不修改 WPF、不实现音频或真实设备强身份关联。

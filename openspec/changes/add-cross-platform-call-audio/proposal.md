# Proposal

## Why

CellPort 目前只有 AT 呼叫控制，不能通过电脑麦克风/扬声器完成双向通话；现有状态判断也不足以安全驱动媒体。已连接的 QDC507 提供了真实的 root ADB 和 USB 基线，但 CLCC 包含非语音 active 条目，需要将修订方案落实为可验证的会话、恢复和跨平台契约。

## What Changes

- 增加完整 CLCC 跟踪、语音/数据分类、呼叫代次和新鲜度门控；AT 命令成功不再直接等同于接通或挂断。
- 增加 USB 父设备身份、无可靠 ADB serial 时的匹配、热插拔和计划内重枚举协调，防止控制/音频连到不同模块。
- 增加固定版本运行时导入、兼容性准入和设备侧 supervisor；驱动/校准/路由具有明确所有权和恢复账本。
- 拆分 USB 音频待机与单次通话媒体，支持精确快照恢复、租约及呼叫/数据占用时延期恢复；禁止为了准备音频自动挂断当前连接。
- 增加 Windows WASAPI 双向媒体、麦克风权限和本地设备选择、有界队列、漂移控制及静音；保留仅呼叫控制模式。
- 基于前置 `isolate-platform-dependencies` 的平台无关核心扩展音频接口，保留 WPF Windows 界面；增加 Linux CLI/PipeWire 接入，并交付 macOS/CellDock 可使用的同协议及一致性验收材料。CellDock 源码修改属于外部仓库接入，不在本仓库直接编辑。
- 增加逐平台兼容记录、诊断、故障注入和发布门槛。helper 无 USB 副作用、驱动兼容性及真实双向通话均是发布前验证项，不因 root/内核版本匹配而提前标记支持。

首版限一个选定模块和一个普通语音呼叫，耳机为质量基线；不实现会议混音、宽带语音、自动刷固件、持久 USB 改写、远程音频或统一跨平台 GUI。

## Capabilities

### New Capabilities

- `call-state-tracking`: CLCC 完整快照、语音/数据区分、呼叫代次、命令结果与网络状态分离。
- `module-device-identity`: 物理 USB 归属、ADB/AT/UAC 关联、模糊匹配拒绝和跨重枚举身份核验。
- `module-audio-runtime`: 兼容 profile、资源导入/部署、驱动/校准/route 所有权和版本化设备控制协议。
- `usb-audio-session`: 待机会话、操作互斥、USB 事务、lease、精确恢复和恢复冲突处理。
- `call-media-bridge`: 四端点双向媒体、门控/静音、格式/队列/漂移、媒体故障和权限处理。
- `cross-platform-audio-hosts`: Windows/Linux 接入、macOS 互操作契约、诊断和平台独立验收。

### Modified Capabilities

无。`openspec list --specs` 当前为空；上述是首次建立行为规范，而非声明这些行为已实现。

## Impact

- 前置 change：`isolate-platform-dependencies` 承担现有 WMI/WinUSB/串口发现隔离和宿主装配。其保留 .NET 9；本音频 change 继续负责 .NET 10 升级、Audio.Core、设备强身份与音频适配。离线协议/fixture 工作可并行于前置实施，平台集成须在前置完成后进行。

- 代码：`CellPort.Core/Services/CallService.cs`、`ModemManager.cs`、`At/AtEngine.cs`、`Transport/*`，以及 WPF `CallsPage`、eSIM/重启/控制台操作入口。
- 新增：平台无关音频会话/协议、Windows 设备与音频适配、Linux CLI/PipeWire 适配、模块侧 supervisor 与测试设施；升级 .NET 目标和打包需独立回归现有 SMS/eSIM 功能。
- 设备：需要经过 profile 验证的 root ADB、模块 ARMv7 运行时与系统 UAC 支持；USB 切换会暂时影响复合设备其他接口，必须受协调器控制。
- 依赖：第三方 `.ko`/helper 的来源、可再分发与新 ABI 是明确前置条件，不从非商业代码仓直接复制实现至本 MIT 项目。
- 证据：只读实测与边界见 `design.md`；当前存在两条 `stat=0, mode=1` 条目，禁止将其当语音呼叫或自动清除。当前未加载音频驱动、未切换 USB、未拨测。
- 交付范围：本 change 仅在 CellPort 仓库实施；外部 CellDock 接入与 macOS 实测独立记录，未通过前不宣称三平台音频均已支持。

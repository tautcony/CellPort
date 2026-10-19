# Design

## Context

见 proposal.md。当前 ModemManager 内部含 WMI 蓝牙过滤和串口探测；ModuleScanner 与 UsbAtChannel 使用 Windows 原生接口；SerialAtChannel 仅依赖 System.IO.Ports。现有 AT 初始化会写设备，自动回归必须使用模拟通道，不触碰当前连接的模块。

## Goals / Non-Goals

目标是 Core 可在非 Windows 构建和测试，同时现有 Windows 产品功能、连接优先级、端口记忆、诊断文案及初始化命令不变。不修改呼叫状态、短信/eSIM 协议、驱动安装 UI、扫描去重与 StableKey 语义。后两者现有局限由音频设备身份 change 处理。

## Decisions

1. 依赖方向为 App/Windows 工具 → Platform.Windows → Transport.Serial → Core；Platform.Windows 也直接引用 Core。Core 和 Transport.Serial 使用 net9.0，Windows 宿主保持 net9.0-windows。相较整体升级 .NET 10，本次不改变安装器运行时要求。
2. Core 增加 IAtChannelProvider：尝试打开 USB、发现串口、构造串口通道、提供端口名称比较器。沿用 IAtChannel。ModemManager 必须构造注入，不用静态全局注册或反射自动选择 OS。端口字符串原样传递；Windows 保留不区分大小写，其他宿主可选择 Ordinal。
3. WindowsAtChannelProvider 承接现有自动探测完整实现（历史端口优先、蓝牙过滤、并行探测、30 秒 WMI 缓存）。USB 和发现的 4/20 秒上限仍由 manager 控制；串口 3 秒打开上限、探测次数和通道释放保持原样。本次不顺便修复原有超时后台任务生命周期问题。
4. ModuleScanner 和其 Windows 诊断结果整体迁入 Windows 命名空间，保留 static 扫描兼容方式；WPF 的设备页和驱动对话框本身属于 Windows UI，无需假造可移植 UI 接口。ModuleInterface/ModuleIdentity 仍是 Core 数据模型，其旧身份字段不用于新跨平台身份绑定。
5. SerialAtChannel 独立到 Transport.Serial，保留传输和枚举 API，不把通用 SerialPort IO 误标为 Windows-only。该程序集可移植不等于已完成 Linux/macOS 硬件验证。
6. 无设备回归覆盖 USB 优先、USB 异常回退、指定端口绕过发现、历史端口/大小写策略、打开失败释放、初始化/状态事件和断开。通过项目引用/程序集依赖测试防止 Core 再引入平台实现。

## Risks / Trade-offs

- 构造函数/具体类型命名空间改变 → 更新仓库所有 App/工具入口，完整编译和发布验证；不承诺外部二进制 API 兼容。
- 迁移算法时发生行为漂移 → 移动原实现，保留超时/缓存/排序和命令代码；使用 diff 比对。
- 本机只能执行 Windows 测试 → 增加 Windows/Linux/macOS CI，验证记录明确区分本地结果和待 CI 执行项，不能声称跨平台真机已支持。
- 短信/eSIM 真机测试可能改变设备状态 → 只执行离线测试及构建，不运行 E2E/EsimProbe。发布前人工 UI/硬件冒烟另列清单。

## Migration Plan

记录原有测试基线，移动平台实现并更新宿主装配，然后运行离线回归、全部项目构建及 Windows 两种发布。产物必须携带两个新程序集。若需回滚，整组撤销项目引用、类型移动和注入改动；无配置或数据迁移。音频 change 引用本前置结果，只保留 .NET 10、Audio.Core、强身份和音频后端工作。

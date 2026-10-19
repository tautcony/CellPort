# Spec Delta

## Purpose

使 Windows 客户端、Linux 命令行宿主和 macOS 协议接入遵循一致的会话行为，同时明确各自的权限、驱动与验证边界，并提供可移植诊断和逐平台发布依据，避免把单平台成功误报为全面支持。

## ADDED Requirements

### Requirement: Preserve Windows control-only operation
Windows 客户端 SHALL 在音频资源、权限或兼容条件不足时保留已有 AT 呼叫控制，并分别呈现呼叫、模块待机、媒体及恢复状态。用户 MUST 能选择本地输入/输出、静音、重试准备和释放音频。

#### Scenario: Runtime is not installed
- **WHEN** 用户打开电话页但没有可用运行资源
- **THEN** 客户端明确显示电脑音频未就绪，已有 AT 控制仍可使用，不显示音频已连接

### Requirement: Preserve composite device driver boundaries
Windows 音频接入 SHALL 保留复合设备的 AT、ADB、网络和系统音频驱动边界，MUST NOT 为启用音频将整个复合设备或音频子接口改绑为原始 USB 控制驱动。

#### Scenario: Audio endpoint fails to enumerate
- **WHEN** 已准备模块但系统没有可用音频端点
- **THEN** 系统报告描述符/驱动/端点诊断，不执行整设备驱动替换

### Requirement: Provide a Linux host without a Windows UI dependency
Linux 宿主 SHALL 提供设备检查、准备、状态、通话和释放的命令行入口，遵守相同的身份、呼叫和恢复契约；只读检查 MUST NOT 自动安装、拨号或切换 USB。

#### Scenario: User runs inspection with no audio permissions
- **WHEN** Linux 用户执行只读检查且缺少设备或音频权限
- **THEN** 输出结构化的权限诊断，不要求整个应用长期以 root 运行，也不全局开放 USB 权限

#### Scenario: Another service owns the AT device
- **WHEN** Linux 的目标 AT 通道被其他服务占用
- **THEN** 系统报告目标设备占用，不全局停用服务或静默切换到另一模块

### Requirement: Publish a portable client interoperability contract
共享协议、状态和脱敏测试向量 SHALL 可由 macOS 客户端使用且不依赖 Windows 类型。CellDock 外部接入 MUST 独立声明版本兼容和验证结果；本仓库协议通过不代表外部客户端已实现。

#### Scenario: External macOS client uses incompatible protocol
- **WHEN** 客户端主协议版本与模块运行时不兼容
- **THEN** 控制被明确拒绝，不启动半兼容会话

#### Scenario: Windows passed but macOS integration is absent
- **WHEN** 当前发布仅有 Windows 真机记录
- **THEN** 支持清单只标记 Windows 已验证，macOS 保持待接入或待验证

### Requirement: Produce privacy-preserving diagnostics
各宿主 SHALL 提供可关联的阶段、错误、兼容 profile、版本、USB 恢复和媒体计数诊断，默认 MUST NOT 保存 PCM、完整电话号码、设备敏感标识、会话密钥或 ADB 凭据。

#### Scenario: Export diagnostics for a failed call
- **WHEN** 用户导出诊断
- **THEN** 输出包含失败阶段、错误码和脱敏呼叫/设备代次，不包含通话内容或完整电话号码

### Requirement: Gate releases by platform-specific evidence
平台支持声明 SHALL 关联 OS/CPU、固件、运行资源和音频设备模式，并通过冷启动恢复、真实呼入/呼出、连续呼叫、长稳及故障恢复验收；模拟测试和端点枚举 MUST NOT 替代真实双向验收。

#### Scenario: Driver loads but no voice call was tested
- **WHEN** 仅证明 root、驱动或 PCM 节点可用
- **THEN** 系统将该组合标记为实验或待验证，不能列为双向通话已支持

#### Scenario: Architecture was built but not exercised
- **WHEN** ARM64 或另一个 OS 版本仅通过编译
- **THEN** 发布记录区分编译支持与真机验证，不继承其他架构的音频认证

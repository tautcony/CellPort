# Spec Delta

## Purpose

为模块侧音频组件提供明确的兼容准入、资源完整性、运行所有权和版本化控制契约，使各宿主能够准备和使用同一设备运行时，同时保留可诊断的失败状态与可重复执行的恢复能力。

## ADDED Requirements

### Requirement: Require an approved compatibility profile
系统 SHALL 在设备修改前核验 root 控制权限、型号、固件、内核构建、模块 ABI、厂商库及运行资源 profile。仅内核 release 相同或已有声卡 MUST NOT 被视为充分的兼容证明。

#### Scenario: Kernel release matches but build is unverified
- **WHEN** 设备报告 3.18.44 但固件或内核构建不在已验证 profile 内
- **THEN** 系统报告未验证兼容，不自动加载音频组件，保留只读诊断和 AT 控制

#### Scenario: Root access is unavailable
- **WHEN** 受支持的控制连接无法获得所需设备权限
- **THEN** 音频准备失败并给出明确前置条件，不上传或加载驱动

### Requirement: Verify runtime resources before activation
系统 SHALL 验证资源来源、版本、大小和摘要，并在宿主及设备端分别校验后才激活。导入失败、部分上传或 helper ABI 不匹配 MUST NOT 进入运行态。

#### Scenario: Interrupted transfer
- **WHEN** 上传中断留下不完整资源
- **THEN** 该资源不被执行或加载，重试不会把部分文件当成有效运行时

#### Scenario: Helper from another bundle is renamed
- **WHEN** 导入文件名符合预期但摘要或 ABI 不匹配
- **THEN** 系统拒绝该资源，而不是仅按名称接受

### Requirement: Own only verified runtime resources
系统 SHALL 在取得设备全局所有权后创建运行资源，并且只停止由会话及进程启动身份共同证明归属的进程。未知来源驱动、校准服务或路由 MUST NOT 被擅自替换。

#### Scenario: Vendor calibration is already running
- **WHEN** 存在未与当前会话协调的校准服务
- **THEN** 系统报告占用，不按进程名终止或重启该服务

#### Scenario: Recorded process ID was reused
- **WHEN** 记录的 PID 对应的启动时间或可执行文件已改变
- **THEN** 清理不向该进程发送终止信号，并保留所有权冲突诊断

### Requirement: Keep call routes independent from USB configuration
可发布的运行时 SHALL 保证单次语音路由的启停不更改 USB functions、gadget enable 或 audio enable；校准和目标 PCM 就绪仍须被独立确认。不满足该契约的 helper MUST 阻止该 profile 的正式音频启用。

#### Scenario: Helper toggles USB audio during exit
- **WHEN** 验证发现 helper 停止时关闭 audio enable 或触发重新枚举
- **THEN** 该 helper 不通过能力准入，系统不退回接通后重枚举的路径

#### Scenario: PCM running without calibration
- **WHEN** PCM 显示 RUNNING 但本次校准未确认或存在关键 DSP 错误
- **THEN** 系统不报告运行时已可用于通话

### Requirement: Provide versioned idempotent session control
设备控制协议 SHALL 明确版本、请求 ID、owner token、boot/session generation、当前阶段与结构化错误。重复的变更请求 MUST 返回原结果或可核验现态；旧所有者或不兼容版本请求 MUST 被拒绝。

#### Scenario: Reply is lost after successful prepare
- **WHEN** 宿主重发同一请求 ID
- **THEN** 设备返回相同会话的结果，不重复加载组件、覆盖初始快照或切换 USB

#### Scenario: Old client sends a route request
- **WHEN** 请求中的 owner、boot 或 generation 已过期
- **THEN** 设备拒绝请求，不影响当前会话

### Requirement: Preserve uncertain kernel state until reboot
系统 SHALL 在释放或部分失败后保留已加载 APR/voice 驱动至模块重启，不热卸载；无法确认安全停止的用户态路由 SHALL 显示恢复未完成，不自动重启模块。

#### Scenario: Second module fails after the first loaded
- **WHEN** 运行时只完成部分驱动加载
- **THEN** 系统记录已加载部分和失败原因，不通过热卸载伪造回滚完成

#### Scenario: Route does not exit gracefully
- **WHEN** 已拥有的路由超过有界正常退出等待时间
- **THEN** 系统报告 recovery_required，不强杀后宣布恢复成功

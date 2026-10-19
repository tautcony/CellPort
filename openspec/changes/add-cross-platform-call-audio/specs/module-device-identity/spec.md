# Spec Delta

## Purpose

将宿主可见的控制接口和音频端点可靠地关联到用户选定的物理模块，在序列号缺失、复合设备接口变化和 USB 重枚举时维持正确归属，避免误操作其他设备或向错误端点发送通话音频。

## ADDED Requirements

### Requirement: Bind control and audio to one physical module
系统 SHALL 用 USB 父设备、拓扑位置、可靠序列号和可读取的启动身份关联 AT、ADB 与模块双向音频。名称、接口号、COM 路径或音频端点 ID MUST NOT 单独构成永久身份。

#### Scenario: Several devices share a friendly name
- **WHEN** 多个音频设备都名为 AC Interface 或 AS Interface
- **THEN** 只选择能追溯至选定 USB 父设备的端点，无法关联则拒绝启动媒体

#### Scenario: Interface numbering changes
- **WHEN** USB 组合改变导致 AT COM 号或接口号变化
- **THEN** 系统重新发现并核验接口，不按旧路径向其他接口发送命令

### Requirement: Handle unusable ADB serials explicitly
系统 SHALL 将空值、问号或重复 ADB serial 视为不可靠，并在确认物理关联后使用当前连接的明确 transport 选择器。不可靠 serial MUST NOT 被当作跨重连身份。

#### Scenario: ADB serial is a question mark
- **WHEN** 唯一候选的 ADB 列表显示 serial 为问号，且其物理关联已确认
- **THEN** 控制请求限定到该候选当前 transport，记录 serial 不可靠，不使用裸设备默认选择

#### Scenario: Multiple ADB candidates are ambiguous
- **WHEN** 两个候选具有相同无效 serial 且无法区分物理归属
- **THEN** 系统报告身份有歧义，要求明确设备选择，不逐台尝试修改

### Requirement: Revalidate identity after re-enumeration
系统 SHALL 在重枚举后重新定位控制通道并核对 boot ID、会话 token 和兼容身份，随后才能绑定媒体。旧连接的句柄、缓存和 transport 选择器 MUST 失效。

#### Scenario: Device returns with a new transport identifier
- **WHEN** 同一 USB 位置的模块重新出现，但 ADB transport ID 改变
- **THEN** 系统重新取得选择器并核对会话，不继续使用旧 transport ID

#### Scenario: Module rebooted during reconnect
- **WHEN** 控制通道恢复但 boot ID 已改变
- **THEN** 旧会话失效，不在新启动实例重放旧 USB 快照或恢复旧媒体

### Requirement: Fail closed on physical identity changes
系统 SHALL 在选定模块被移除、移动端口或被其他设备替代而无法证明身份连续性时关闭媒体并报告重新选择需求。

#### Scenario: Another module occupies the old port
- **WHEN** 新候选出现在原拓扑位置但启动/设备身份不匹配
- **THEN** 系统不发送旧会话控制命令，不把它自动纳入当前通话

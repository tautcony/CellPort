# Spec Delta

## Purpose

管理可复用的模块 USB 音频待机与恢复事务，在音频准备、网络连接及呼叫之间建立明确的操作边界，并在宿主失联或失败后区分音频停止、恢复延期和已确认恢复，避免丢失用户原有配置。

## ADDED Requirements

### Requirement: Coordinate destructive device operations
系统 SHALL 对同一模块的音频准备/释放、USB 网络切换、eSIM 修改、模块重启和原始控制台破坏性命令进行统一互斥。首版只有新鲜且完整的空 CLCC 快照允许破坏性 USB 准备。

#### Scenario: Active data entries block preparation
- **WHEN** CLCC 含 stat=0、mode=1 条目且尚需 USB 重枚举
- **THEN** 系统报告数据连接占用并暂停准备，不误报语音通话，也不自动发送挂断、数据断开或复位命令

#### Scenario: Call arrives during preparation
- **WHEN** 来电发生在 USB 事务提交前
- **THEN** 系统取消尚未执行的 USB 修改；若事务已经开始，则由设备端收敛到已定义的一致状态并报告结果，不留下无期限禁用的 gadget

#### Scenario: Conflicting operation is requested
- **WHEN** 音频会话持有设备且另一入口请求重启或改变 USB
- **THEN** 系统拒绝或延后该操作，并说明当前占用；控制台不得绕过该约束

### Requirement: Separate standby from call media
系统 SHALL 在空闲窗口完成 USB 音频准备，并在挂断后保持待机以复用下一通；待机 MUST NOT 采集本地麦克风或传输通话内容。单次呼叫开始/结束 MUST NOT 重新配置 USB。

#### Scenario: Second call uses prepared endpoints
- **WHEN** 第一通结束后用户再次拨号且待机仍有效
- **THEN** 系统复用 USB 待机，仅启动新的路由和媒体代次

#### Scenario: Incoming call arrives before readiness
- **WHEN** 来电时准备未完成且还需要重新枚举
- **THEN** 系统提供明确的仅呼叫控制状态，不在响铃或通话中切换 USB

### Requirement: Persist recoverable original configuration before mutation
系统 SHALL 在第一次设备修改前保存原始 functions、gadget enable、audio enable、boot/owner 身份及修改进度，并建立跨 ADB 断链存活的设备监督。恢复所需记录 MUST 在结果不确定时保留。

#### Scenario: USB write succeeds but host loses reply
- **WHEN** USB 写入已经生效而宿主应答丢失
- **THEN** 恢复根据设备账本及实际值判定完成步骤，不覆盖原始快照或盲目重复切换

#### Scenario: Supported baseline already includes audio
- **WHEN** 原始 functions 含 audio 且已通过所有权准入
- **THEN** 准备不会重复追加 audio，释放保留进入前的 audio 配置

### Requirement: Restore exact values with ownership checks
系统 SHALL 在原 boot 和所有权仍有效且配置未被外部改写时恢复所有被修改字段的原始值。MUST NOT 硬编码恢复为特定网卡组合、enable=1 或 audio enable=0。

#### Scenario: Original enable is disabled
- **WHEN** 一个明确支持该状态的 profile 释放原 enable=0 的会话
- **THEN** 最终 enable 仍为 0，不为了宿主重连而改写为 1；未验证此状态的 profile 在准备前拒绝

#### Scenario: External process changes configuration
- **WHEN** 恢复时当前字段不同于本事务最后应用的已知状态
- **THEN** 系统报告恢复冲突并保留记录，不覆盖外部修改

#### Scenario: Restored configuration removes control connectivity
- **WHEN** 原配置恢复后宿主无法再立即访问设备
- **THEN** 设备保存恢复凭据，宿主显示待核验而非伪造已恢复，下次连接读取凭据

### Requirement: Expire leases without unsafe USB recovery
系统 SHALL 使用设备单调时间计算租约，正常失联 45 秒后进入停止音频和清理流程；计划内初次准备宽限 MUST 有最长 120 秒边界。对于已处于一致可连接 USB 状态的会话，存在呼叫/数据占用或无法独立确认空闲时 MUST 延期会再次断链的 USB 恢复，不自动挂断连接。已经开始且尚未收敛的事务须先完成有界本地收敛。

#### Scenario: Host is lost while occupancy is unknown
- **WHEN** 租约到期且设备侧无法取得可靠的新鲜空闲证据
- **THEN** 系统停止本会话音频并进入 restore_pending_call，保留占用或未知原因及最小配置，等待重连核验

#### Scenario: Host is lost and independent idle is confirmed
- **WHEN** 租约到期且受支持的设备侧方式确认无占用
- **THEN** 系统在到期后的 15 秒清理预算内完成恢复或明确报告失败，不无限延长租约

#### Scenario: Host clock changes
- **WHEN** 宿主墙钟前后跳变
- **THEN** 设备租约截止不受影响，只有有效续租收到的设备时间更新租约

### Requirement: Converge interrupted USB transactions locally
系统 SHALL 在开始 disable/write/enable 后以有界设备端流程完成提交或恢复原可连接配置，不依赖宿主在禁用期间提供新的呼叫状态。MUST NOT 将暂时禁用且未完成的事务当作可无限保留的延期状态。

#### Scenario: Control disconnects while gadget is disabled
- **WHEN** 本事务已禁用 gadget 且后续写入失败或宿主连接丢失
- **THEN** 仍存活的设备监督完成当前最小一致事务或回滚原配置，不等待不可达宿主的 CLCC；失败明确记录，不宣称无条件可恢复

### Requirement: Recover repeatably after interruption
系统 SHALL 支持重复释放/恢复且不重复创建资源；模块重启后旧恢复失效。监督进程自身死亡时 SHALL 保留可在重连后恢复的账本，不宣称仅凭退出回调可覆盖强杀。

#### Scenario: Supervisor dies unexpectedly
- **WHEN** 监督进程被异常终止
- **THEN** 下一次连接识别未完成会话、校验归属并恢复或报告人工恢复需求，不显示待机正常

#### Scenario: Release is repeated
- **WHEN** 已恢复的会话再次收到释放请求
- **THEN** 系统返回已恢复结果，不启动路由或再次修改 gadget

### Requirement: Distinguish shutdown from network hangup
释放音频 SHALL 只停止媒体并按占用状态决定 USB 恢复，不隐式挂断网络呼叫；系统 SHALL 分别呈现通话控制、音频待机、媒体和恢复状态。

#### Scenario: User releases audio during a call
- **WHEN** 用户点击释放音频而网络语音仍 active
- **THEN** 媒体停止、USB 恢复延期，界面仍显示该呼叫并允许用户主动挂断

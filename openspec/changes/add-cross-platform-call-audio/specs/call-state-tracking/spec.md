# Spec Delta

## Purpose

建立可靠的蜂窝呼叫状态契约，让界面和音频链路依据完整且及时的模块状态行动，明确区分普通语音、数据呼叫和未知状态，并防止旧呼叫的媒体被发送到后续呼叫。

## ADDED Requirements

### Requirement: Classify complete call snapshots
系统 SHALL 仅将成功结束且关键字段全部可解析的 CLCC 响应视为有效快照，区分普通语音 mode=0、数据 mode=1 和不支持的其他模式。非语音条目 MUST NOT 使语音状态变为 Active 或启动麦克风。

#### Scenario: Connected device reports active data calls
- **WHEN** 响应包含两条 stat=0、mode=1 的条目并以 OK 结束
- **THEN** 系统报告两个非语音占用和无已确认普通语音呼叫，不启动语音媒体，不自动挂断数据连接

#### Scenario: Voice and data coexist
- **WHEN** 有效快照同时包含 mode=0 的 active 呼叫和 mode=1 条目
- **THEN** 系统只将选定语音条目用于媒体判定，同时保留数据占用信息供 USB 会话使用

#### Scenario: Failed query is not an empty list
- **WHEN** 查询超时、ERROR 或任一关键呼叫字段损坏
- **THEN** 系统报告 Unknown 而非空闲，不使用不完整列表证明 USB 可切换

#### Scenario: Successful empty response confirms no calls
- **WHEN** 查询成功结束且没有 CLCC 条目
- **THEN** 系统报告已确认的空列表

### Requirement: Confirm network state independently of command acceptance
系统 SHALL 将拨号、接听和挂断命令结果与网络呼叫状态分离。只有匹配的 active 语音快照才能确认接通；挂断后没有目标呼叫才能确认该呼叫结束。未知含义的 URC MUST NOT 直接确认为接通。

#### Scenario: Answer command succeeds before connection
- **WHEN** ATA 返回 OK 但最新有效快照仍为 incoming
- **THEN** 界面维持待确认接通状态，麦克风上行保持关闭

#### Scenario: Hangup is not confirmed
- **WHEN** 用户挂断而命令失败或 CLCC 无法查询
- **THEN** 本地媒体立即关闭，界面显示挂断未确认，并保留后续查询能力

#### Scenario: Unmapped indicator arrives
- **WHEN** 收到尚未按固件映射的 CIEV 指示
- **THEN** 系统刷新完整快照，不凭最后一个数字设置语音 Active

### Requirement: Isolate call generations
系统 SHALL 为每次可区分的语音呼叫分配独立代次，将其与模块会话、index 和方向绑定。跨结束、模块重启或无法证明连续性的断链 MUST NOT 复用旧媒体授权或缓冲。

#### Scenario: Call index is reused
- **WHEN** index=1 的呼叫已结束，随后另一呼叫也使用 index=1
- **THEN** 新呼叫获得新的代次，旧启动响应和媒体回调无效

#### Scenario: Identity cannot be proven after reconnect
- **WHEN** 控制断链后重新出现同 index 的 active 条目，但无法证明仍是原呼叫
- **THEN** 系统不自动恢复旧麦克风上行，不重放旧 PCM

### Requirement: Bound call-state staleness
系统 SHALL 在状态变化后及时刷新 CLCC，并在 active 期间保持有界的新鲜度；首版新鲜度上限为三秒。超过上限、held、目标改变或不支持的多语音呼叫出现时 MUST 关闭上行门控。

#### Scenario: Background command starves call tracking
- **WHEN** 长操作导致语音快照超过三秒未更新
- **THEN** 媒体停止发送，显示状态未知，而不是继续依据过期 Active 发送

#### Scenario: Held or multiple voice calls
- **WHEN** 目标变为 held 或快照出现无法唯一选择的多语音呼叫
- **THEN** 系统暂停媒体，不自动切换到列表中的第一个 active 呼叫

### Requirement: Record confirmed call duration and direction
系统 SHALL 保存呼叫创建时的方向，并以确认 active 到确认结束的区间计算通话时长；未确认接通的呼叫不得获得已接通时长。

#### Scenario: Outgoing call finishes from Active state
- **WHEN** 呼出在 Active 状态结束
- **THEN** 记录仍为呼出，时长不包含拨号等待区间

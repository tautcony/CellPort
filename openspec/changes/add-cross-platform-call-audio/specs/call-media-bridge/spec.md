# Spec Delta

## Purpose

为用户提供绑定到选定模块和本地音频设备的双向通话媒体，保证上行只在正确呼叫内启用，并在权限、格式、设备或时钟异常时保持有界延迟和可解释的失败状态，防止跨呼叫残留音频。

## ADDED Requirements

### Requirement: Route four distinct endpoints
系统 SHALL 将本地麦克风送到模块输出端点，并将模块输入端点送到本地扬声器或耳机；本地与模块端点必须分别选择。应用 MUST NOT 改写系统默认设备或把模块误当本地输入输出形成环路。

#### Scenario: System selects module as default speaker
- **WHEN** 系统默认播放设备变为模块 UAC
- **THEN** 应用拒绝将其同时用作本地下行播放目标，并要求选择合适的本地设备

#### Scenario: A headset is removed
- **WHEN** 选定本地耳机在通话中消失
- **THEN** 媒体先静音并报告设备丢失，不未经选择悄悄切换到外放

### Requirement: Gate media by call identity and readiness
系统 SHALL 仅在当前代次语音 active、运行时路由就绪、四端点归属/格式有效且所需权限已授予时启用媒体。媒体启动初始为静音，流推进后须再次确认呼叫；待机或不匹配呼叫 MUST NOT 采集并发送本地麦克风。

#### Scenario: Call ends while media is starting
- **WHEN** 路由已启动但媒体开启前目标呼叫结束
- **THEN** 系统取消本次启动、清空缓冲，麦克风不向新呼叫发送

#### Scenario: Microphone permission is denied
- **WHEN** 用户拒绝或撤销麦克风权限
- **THEN** 上行关闭并显示权限原因，AT 控制仍可用；仅收听必须明确显示而非标为完整双向音频

### Requirement: Enforce mute at the outgoing media boundary
用户静音 SHALL 立即在宿主上行出口生效，并清除待发送的麦克风数据；设备要求连续传输时只发送数字静音。设备静音命令失败 MUST NOT 导致麦克风继续发送。

#### Scenario: Modem mute command is unsupported
- **WHEN** 用户开启静音但 AT 静音命令失败
- **THEN** 宿主上行仍为静音，界面区分本地静音有效和设备命令结果

### Requirement: Negotiate formats with bounded buffering
系统 SHALL 按端点实际格式完成声道和采样率转换；当前 profile 的模块媒体为 8 kHz 单声道 PCM16LE。每方向排队时间 MUST 有 120 ms 上限，欠载补静音，过载丢弃过旧数据并计数；首轮本地新增单向延迟目标为 p95 不超过 150 ms，不含蜂窝网络。

#### Scenario: Host format differs from module format
- **WHEN** 本地设备返回 44.1 或 48 kHz float 多声道格式
- **THEN** 系统正确下混、滤波重采样及转换为模块格式，不把字节直接当 PCM16 发送

#### Scenario: Consumer falls behind
- **WHEN** 播放或传输消费速度短时下降
- **THEN** 队列不超过约定时长，旧音频被丢弃并记录，不持续增长为秒级延迟

### Requirement: Compensate independent clocks and detect stalled streams
系统 SHALL 处理模块与本地设备的独立采样时钟，报告帧数、时间戳、水位及补零/丢帧。持续一秒无流推进 SHALL 触发媒体故障；持续零振幅但流仍推进 MUST NOT 被误判为断流。

#### Scenario: Long call has clock drift
- **WHEN** 设备间存在持续采样时钟偏差
- **THEN** 系统补偿偏差并维持有界水位，不靠周期性长时间停顿清空缓冲

#### Scenario: Remote party is silent
- **WHEN** 样本为零但回调时间和帧数继续推进
- **THEN** 媒体保持健康，不以静音为由重启路由

#### Scenario: One direction stops advancing
- **WHEN** 任一必要方向超过一秒无流推进
- **THEN** 系统关闭上行并报告媒体故障；重试必须重新确认目标呼叫且次数有上限

### Requirement: Reset all media state between generations
系统 SHALL 在挂断、失效或呼叫代次变化时停止采集并清除所有上行、下行和处理器历史；旧回调 MUST NOT 向新通话贡献音频。

#### Scenario: Buffered speech survives a rapid redial
- **WHEN** 挂断后立即拨打下一通且旧回调稍后到达
- **THEN** 旧缓冲和回调均被丢弃，新的媒体从静音状态开始

### Requirement: Advertise audio quality only for tested device modes
系统 SHALL 以耳机为首轮双向基线，将免提和蓝牙质量分别标注；免提发布需独立验证回声消除和双讲，非零样本或模块内回声处理 MUST NOT 单独作为电脑免提通过依据。

#### Scenario: Headset testing passes but speakerphone is untested
- **WHEN** 平台仅完成耳机双向验证
- **THEN** 支持清单不宣称免提或蓝牙质量已验证

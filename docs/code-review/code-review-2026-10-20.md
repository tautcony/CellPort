# CellPort 代码审核报告（含修复记录）

- 审核对象：`codex/cross-platform-call-audio-wip` 分支工作区（含未提交改动）
- 审核方式：**静态阅读**；首轮修复后曾构建并运行测试。本轮追加修正未重新构建或运行测试，历史结果与本轮结果分开说明，见第七节。
- 代码规模：`src/` 下 148 个 C# 文件、约 14.9k 行；`tests/` 约 5.2k 行
- 审核范围：CellPort.Core（AT/PDU/服务）、Transport（USB/串口）、Platform.*（Win/Mac/Linux）、Audio.Core、App（UI/设置/转发）

结论：整体分层与并发设计水平明显高于一般工具类项目（`CallTracker` 的不可变快照、`DeviceOperationCoordinator` 的准入仲裁、`AudioFrameBuffer` 的单生产者单消费者环、`RuntimeManifestValidator` 的路径/符号链接防护都写得相当克制）。但 **短信 PDU 构造存在两处协议级错误，会直接导致"发错号码/丢字"**，另有若干资源泄漏与竞态问题。以下按严重程度排列，每条给出位置、证据、影响与修复建议。

## 修复状态总览（2026-10-20）

| 级别 | 状态 | 说明 |
|---|---|---|
| P0 | ✅ 5/5 已修复 | 短信发送链路全部重写；包含复核时发现的 TP-MR 缺失 |
| P1 | ✅ 5/5 已修复 | 通道泄漏、NRE 竞态、断开清理、CMGS 提示符、时区符号；本轮补齐首次 AT 探测异常清理与 USB 失败回退 |
| P2 | ✅ 19/20 已修复，1 项改为澄清注释 | P2-3 由现有测试锁定行为，见该条说明 |
| P3 | ⬜ 0/8 未处理 | 保留为后续工程化清理清单 |

逐条修复方式见各条的 **✅ 已修复** 标记、「P2 逐项修复记录」与本节补充；历史验证数据见第七节。

**验证边界**：第七节数据记录的是首轮修复后的历史结果，不包含本轮追加修改；本轮未运行构建或测试，因此不能把该历史结果视为本轮改动的验证结论。

### 复核补充修正（2026-10-20）

- **短信收件号码与有效期**：号码只移除空格、括号、短横线和点号；字母、Unicode 数字、非首位或重复的 `+` 会被拒绝，避免静默改写收件人。`TP-VP` 从 `00` 改为 `8F`（12 小时），避免短信在 5 分钟后过期。
- **取消与 AT 响应同步**：普通 AT 命令取消后，在超时范围内继续排空当前响应；CMGS 的提示符、PDU 与最终响应现在处于同一命令租约。取消发生在正文提交前会发送 ESC 并排空取消结果；PDU 开始写入后会完整写入，Ctrl+Z 提交后继续读取最终状态，避免报告取消但短信实际已提交。传输读取失败时立即退出排空并传播取消，避免错误通道上空转。
- **首次通道探测和 USB 回退**：打开通道或首次 `AT` 探测失败/取消时都会释放通道；USB 直控未能通过 AT 探测时，继续探测串口接口。
- **失败状态保留**：短信、设备信息与电话簿服务保留失败的 `AtResult`，调用处只在 `Succeeded` 时解析数据；取消仍向上传播。
- **音频租约与配置保存**：准备操作持有的 CTS 延迟至租约释放；协调器等待信号量超时后安排租约释放时再做清理。配置以进程内锁保护临时文件替换，Unix 权限设置失败会报告保存失败并清理临时文件。
- **eUICC 通知 SSRF**：默认 HTTP handler 在连接时重新解析并拒绝包含非公网 IP 的 DNS 答案，然后直接连到已校验 IP，避免 DNS 校验与连接之间被重绑定；禁用代理和重定向。通过 `EsimNotificationHttpClient` 注入的自定义客户端由注入方负责传输层策略。
- **诊断 UI 阻塞**：连接失败后的平台诊断移到后台任务执行，避免同步枚举阻塞 UI。

这些补充修正尚未由本轮构建或测试验证；P3 仍按原计划保留。

---

## 一、P0：功能正确性缺陷（短信发送路径）

### P0-1 国际号码被多插一个 `9`，短信会发往错误号码 ✅ 已修复

**位置**：`src/CellPort.Core/At/SmsCodec.cs:118-139`、`src/CellPort.Core/Services/SmsService.cs:254-272`

```csharp
// SmsCodec.EncodeNumberBcd
var digits = new StringBuilder();
if (international) digits.Append('9');   // ← 问题所在
digits.Append(number.Replace("+", string.Empty));
```

**事实依据**：3GPP TS 23.040 §9.1.2.5 中，TP-DA 的编码是「地址长度 + TOA（单独一字节）+ BCD 号码」，号码本身**不含任何前缀**。`0x91` 这个 TOA 字节才表示"国际格式"。代码在 `SmsService.BuildPdu` 里已经单独写了 `toa = "91"`，因此这里的 `'9'` 是重复编码。

**后果推导**（纯按代码计算）：`+8613800138000` → 位数 13，BCD 串变成 14 个半字节 `98613800138000` → 7 字节 `89 16 83 00 31 08 00`。接收侧按 13 位解码（低半字节优先）得到 `9861380013800`，即**整体右移一位、首位变 9**。用户只要在号码里带 `+`（界面允许输入），短信就发到了一个不存在的号码。国内号段不带 `+` 时 `international=false`，不会触发——这解释了为什么该缺陷在中文环境下难被发现。

**注意**：`tests/CellPort.Core.Tests/SmsCodecTests.cs:66` 的用例把错误行为写成了期望值（`"+8613800138000" → "89168300310800"`），且注释自己说明"international 会补 '9' 前缀"。**这条测试不能作为正确性依据，需要同步修正**。

**建议**：删除 `if (international) digits.Append('9')`；补一条独立于实现的规范向量测试（例如 `+19545742336` 的 TP-DA 应为 `0B 91 59 45 47 32 63 F3`）。

> **✅ 已修复**：删除 `'9'` 前缀，国际格式只由 TOA `0x91` 表达。`SmsCodecTests` 中被固化的错误期望（`+8613800138000 → 89168300310800`）一并修正，改用 `+33123456789 → 3321436587F9` 与 `+19545742336 → 5945473263F3` 两组规范向量。

---

### P0-2 GSM 7-bit 短信把「字节数」当「septet 数」写入 TP-UDL，英文短信丢字符 ✅ 已修复

**位置**：`src/CellPort.Core/Services/SmsService.cs:213-219、254-271`

```csharp
var (hex, dcs) = SmsCodec.EncodeForText(body);
var pdu = BuildPdu(normalized, dcs, hex, hex.Length / 2);   // udOctets = 打包后的字节数
...
+ $"{udOctets:X2}"   // 写入 TP-UDL
```

**事实依据**：TP-UDL 的语义随 DCS 变化——UCS2（DCS=0x08）时是**字节数**，GSM 7-bit（DCS=0x00）时是**septet 数**。代码对两种编码都传字节数。

**后果推导**：`n` 个 septet 打包后占 `ceil(7n/8)` 字节。`n=4` 时两者相等（所以 `"AB12"` 这类短用例看不出问题），但 `n≥8` 起 `ceil(7n/8) < n`：
- `"hellohello"`（10 septet，9 字节）→ UDL 写成 `09`，接收方只解出 9 个字符 → `"hellohell"`；
- 30 个字符 → UDL 27，末尾丢 3 字。

**建议**：`BuildPdu` 增加 `udl` 参数；GSM 7-bit 时传 septet 数（需扣除扩展字符占 2 septet 的开销），UCS2 时传字节数。

> **✅ 已修复**：`BuildPdu` 增加 `udl` 参数。GSM7 传 septet 数（带 UDH 时为 `ConcatUdhSeptets(7) + n`），UCS2 传字节数。同时加入 `udl is < 0 or > 255` 越界判断并抛 `ArgumentOutOfRangeException`。`SmsCodecTests` 新增 `UdlOffset` 校验用例，按 septet 边界反查 UDL 字节，确认 `"hellohello"`（10 septet / 9 字节）写入 `0A` 而非 `09`。

---

### P0-3 发送侧没有长短信分段，UDL 超 255 会写出非法 PDU ✅ 已修复

**位置**：`SmsService.SendAsync`（`SmsService.cs:205-249`）与 `BuildPdu`（`:254-272`）

**事实依据**：`BuildPdu` 只用 `$"{udOctets:X2}"` 写一个字节的 UDL，且整个正文作为单个 TP-UD 发送。没有按 153/67 字符（8-bit 参考号 UDH）做分段。

**后果**：
- 中文 >70 字（140 字节）、英文 >160 字即超出单条 SMS 上限，模块回 `ERROR`，界面只显示"发送失败"，用户无从判断；
- 更糟的是 `udOctets > 255` 时 `:X2` 会输出 3 个十六进制字符（如 `190`），**PDU 结构直接损坏**，模块可能返回 `+CMS ERROR: 304` 或静默丢弃。

**建议**：在 `SendAsync` 中按编码类型分段并加 UDH（IEI 0x00/0x08，每段携带 `ref/total/seq`），逐段 `AT+CMGS`；同时给 UDL 加上限校验，超限立即返回明确错误而不是拼出坏报文。

> **✅ 已修复**：新增 `SmsCodec.BuildSubmitPdus` 统一负责分段，采用 8-bit 参考号（IEI `0x00`，6 字节 UDH `05 00 03 <ref> <total> <seq>`），TP-FO 置 `0x51`。GSM7 单段 160 / 分段 153 septet；UCS2 单段 140 / 分段 134 字节。`SplitSeptets` 保证不把「ESC + 扩展码」拆到两段，`ChunkUcs2` 保证不拆开代理对。`SmsService.SendAsync` 逐段发送，任一段失败立即中止。
>
> 注：`BuildUcs2Body` 先按 140 字节判断能否单段，仅超限时才改用 134 字节分段——否则 70 个汉字会被无谓拆成两条。

---

### P0-4 GSM 7-bit 编码器对不支持的字符静默替换为 `?` ✅ 已修复

**位置**：`SmsCodec.cs:35-66`

`Gsm7Lookup.GetValueOrDefault(c, (byte)'?')` 把 `[ ] { } ~ ^ | \ €` 等 GSM 7-bit **扩展表**字符（需要 ESC 0x1B 前缀）以及所有表外 ASCII 一律替换成 `?`，且无任何提示。`RequiresUcs2` 只判断 `c > 0x7F`，因此这类字符既不走 UCS2、也不报错，直接丢内容。一个含 `https://x/[1]` 的通知短信会被改成 `https://x/?1?`。

**建议**：把扩展表字符编码为两 septet（ESC + 码点）并计入 UDL；或者对无法编码的字符直接抛错，让上层改用 UCS2。

> **✅ 已修复**：新增扩展表映射（`[ ] { } ~ ^ | \ €`），`ToSeptets` 编码为 `ESC (0x1B) + 码点` 两个 septet 并计入 UDL；`RequiresUcs2` 改为基于 `CanEncodeGsm7` 判断，能编码的扩展字符不再误判为需要 UCS2；表外字符不再静默替换为 `?`，直接抛 `ArgumentException` 由上层记录 `AppLog.Warn`。`SmsCodecTests` 新增含 `[ ] { } ~` 的往返用例。

### P0-5（复核补充，原文未列）SMS-SUBMIT 缺少 TP-MR 字节，整条 PDU 字段错位 ✅ 已修复

**位置**：`src/CellPort.Core/At/SmsCodec.cs` 的 `BuildPdu`

**发现过程**：P0-1~P0-4 修复后，`BuildSubmitPdus_SingleSegment_RoundTripsThroughParser` 仍全部失败，异常为 `InvalidOperationException: PDU 数据不足。`。逐字节比对发现 `SMSC长度 + TP-FO` 之后直接写了 TP-DA，**漏掉 TP-MR（消息参考号）**，导致后续所有字段右移一个字节。

**后果**：这是协议级错误。模块会把 TP-DA 长度当作 TP-MR、把 TOA 当作地址长度，整条 PDU 无法被接收方正确解析——即使 P0-1~P0-4 全部修好，短信依然发不出去。属于比 P0-1/P0-2 更致命的一类问题。

**修复**：在 `firstOctet` 之后补 `"00"`（TP-MR），完整字段序变为 `SMSC长度 → TP-FO → TP-MR → TP-DA长度 → TOA → BCD → TP-PID → TP-DCS → TP-VP → TP-UDL → TP-UD`。测试同步校正：`UdlOffset` 由 `4 +` 改为 `5 +`、`pdu[2]` → `pdu[3]`、`StartsWith("0011")` → `"001100"`、地址段 `pdu[6..8]` → `pdu[8..10]`、单段 PDU 长度 155 → 156。

---

## 二、P1：稳定性 / 资源泄漏 / 竞态

### P1-1 连接探测超时后会泄漏已被打开的通道（macOS 上可能导致"第二次连不上"）✅ 已修复

**位置**：`ModemManager.cs:144-171`、`:184-191`

```csharp
var usbTask = _channels.TryOpenUsbAsync(ct);
var done = await Task.WhenAny(usbTask, Task.Delay(TimeSpan.FromSeconds(4), ct));
if (done == usbTask) { ... } else { Trace("USB 探测超时，转串口。"); }   // usbTask 未取消、未释放
```

`usbTask` 若在 4s 后才完成并返回了非 null 通道，该通道**永远不会被 Dispose**：libusb 句柄与 `claim_interface` 一直持有，winusb 句柄同样。macOS 上接口是独占的，下一次 `libusb_claim_interface` 会失败，表现正是"重试连接失败"。`detectTask`（20s 超时）同样会被遗留在后台继续打开/关闭串口，与随后的真实连接争用端口。

**建议**：超时分支里 `_ = usbTask.ContinueWith(t => t.Result?.DisposeAsync())`（或用一个 `CancelAfter` 链接令牌并在 `TryOpen` 内部保证 finally 释放）；`detectTask` 同理，或改为传入同一个 `cts.Token` 并在超时后 `Cancel()`，同时保证探测协程在 `catch(OperationCanceledException)` 路径上释放已打开的端口。

> **✅ 已修复**：新增 `DisposeChannelWhenCompleted(Task<IAtChannel?>)`，USB 探测落入超时分支后挂接续延——任务后续才完成且返回非 null 通道时立即 `DisposeAsync()`。串口探测改用独立 `detectCts`，超时路径显式 `CancelAsync()`，探测协程的取消路径上 `ObserveDetached()` 释放已打开端口，不再与随后的真实连接争抢同一 tty。

---

### P1-2 `Engine` / `Calls` / `Sms` / `Cusd` 二次读取导致空引用竞态 ✅ 已修复

**位置**：`ModemManager.cs:330-345`、`:357-365`、`:367-376`、`:676-684`

```csharp
if (Engine is null) return new AtResult(...);          // 第一次读
var admission = await TryAcquireOperationAsync(...);    // 这里会让出线程
...
return await Engine.ExecuteAsync(command, ...);        // 第二次读 → 可能已为 null
```

`DisconnectAsync` 在 UI 线程/后台把 `Engine`、`Calls`、`Sms` 置 null，而 `await` 期间上述字段可能被清空。`RunCallControlAsync` 里 `operation(Calls)`、`SendSmsAsync` 里 `Sms.SendAsync`、`SendUssdAsync` 里 `Cusd.SendAsync` 都是同一模式。结果是从界面发起的 AT/USSD/拨号操作在"恰好点断开"时抛 `NullReferenceException`。

**建议**：进方法就把字段捕获到局部变量（`var engine = Engine; if (engine is null) ...`），后续只用局部变量；`ReadCurrentIccidAsync`（`:643-646`）已经这么写了，应统一。

> **✅ 已修复**：`ExecuteAtAsync`、`SendSmsAsync`、`SendUssdAsync`、`RunCallControlAsync`、`RefreshStatusAsync` 全部改为入口即捕获局部变量，`await` 之后不再回读字段。`DisconnectAsync` 并发置空时不再抛 `NullReferenceException`。

---

### P1-3 `DisconnectAsync` 未清理 `Cusd` / `Phonebook`，且不等待泵任务 ✅ 已修复

**位置**：`ModemManager.cs:286-309`

只把 `Sms`/`Calls`/`Info` 置空，`Cusd` 与 `Phonebook` 仍指向已释放的 `AtEngine`；`_pumpTask` 也没有被 await（`_pumpCts` 取消后立即 Dispose，泵协程仍在跑最后一次 `DrainUnsolicited`）。于是断开后 `Cusd != null` 但引擎的 `PrioritizedCommandLock` 已 Dispose，`ExecuteAsync` 会从 `ObjectDisposedException.ThrowIf(_disposed, this)` 抛出——被 `CusdService` 的 `catch (Exception)` 吞掉后变成一条含义不明的错误文本。

**建议**：统一置空全部服务引用；`DisconnectAsync` 内 `await (_pumpTask ?? Task.CompletedTask)` 并加超时；把 `AtEngine.DisposeAsync` 的调用放在所有在飞命令之后（或让 `ExecuteAsync` 在 disposed 时返回 `TransportFailure` 而不是抛异常）。

> **✅ 已修复**：`DisconnectAsync` 重排为「取消并等待泵（2 秒超时兜底）→ 统一置空 `Sms`/`Calls`/`Info`/`Cusd`/`Phonebook` → 最后 `DisposeAsync()` 引擎」。同时采纳备选方案：`AtEngine.ExecuteAsync` 在已释放或已取消时返回 `AtCompletionStatus.TransportFailure`（新增 `int _disposed` + `Interlocked.Exchange`），不再抛 `ObjectDisposedException` 被上层 `catch (Exception)` 吞成含义不明的文本。

---

### P1-4 `AT+CMGS` 用 `OK` 判定 `>` 提示符，每条短信固定多等 5 秒 ✅ 已修复

**位置**：`SmsService.cs:222-228`

```csharp
await SafeAsync($"AT+CMGS={tpduLength}", ct, AtEngine.DefaultTimeout).ConfigureAwait(false);
// 模块实际只回 ">"，不会回 OK；SafeAsync 会一直等到 5s 超时
var sendResult = await SafeAsync($"{pdu}\u001A", ct, AtEngine.LongTimeout).ConfigureAwait(false);
```

附带问题：`AT+CMGS` 的返回值被丢弃，即使模块回 `ERROR`（无服务、号码非法）也照样把 PDU 写下去，等于把正文当 AT 命令行发给模块。

**建议**：为 `AtEngine` 增加"等待指定提示符/中间态"的能力（`ExecuteAsync(..., expectPrompt: ">")`），或在 `ReadUntilFinalAsync` 中把单独一行 `>` 视为中间终结态；`CMGS` 失败时立刻中止并返回明确原因。

> **✅ 已修复**：`AtEngine.ExecuteAsync` 新增 `string? expectPrompt` 重载与 `AtCompletionStatus.Prompt` 完成状态，单独一行 `>` 即视为中间终结态；另加 `TryTakeTrailingPrompt` 处理部分固件只回 `"> "` 且不带换行的情况。`SmsService` 按提示符判定，未出现提示符（模块回 `ERROR`）时立即中止，不再把 PDU 正文当 AT 命令行写进模块；第二段同样检查 `Succeeded`。`SmsSendPipelineTests` 断言不再空等 5 秒、完成状态为 `Prompt`。

---

### P1-5 短信时间戳的时区符号判断恒为假 ✅ 已修复（符号位位置与原文推断不同）

**位置**：`SmsPduParser.cs:270-306`

```csharp
var tzRaw = Dec(raw[6]);              // BCD → 十进制（0..105）
var tzQuarters = tzRaw & 0x7F;
var negative = (tzRaw & 0x80) != 0;   // Dec 的返回值永远 < 128，此条件永假
```

`Dec()` 已经把半字节转成十进制，符号位（原字节 bit3 of 高半字节）在转换中丢失。负时区（美洲/部分欧洲）会被当成正值，时间最多偏差 15 小时；例如 UTC-5 的 `-20` 季度值编码为 `0xA2`，`Dec` 得 30，最终被解析为 +7.5h。

**建议**：在 `Dec` 之前先取原始字节判断符号（`(raw[6] & 0x08) != 0`），再按其规则求季度数；补一个负时区的规范向量测试（`SmsPduParserTests` 目前只有正时区）。

> **✅ 已修复**。原文推导有两处不准确，复核时一并纠正：
>
> 1. **符号位位置**：原文写「bit3 of 高半字节」，按 BCD 半字节交换后的实际读法是「十位半字节的 bit3」，即 `(tzOctet & 0x08) != 0`；配套 `tzTens = tzOctet & 0x07`、`tzUnits = (tzOctet >> 4) & 0x0F`。必须从原始字节读取，不能读 `Dec()` 之后的十进制值。
> 2. **示例向量**：原文称 UTC-5 的 `-20` 季度编码为 `0xA2`，但 `0xA2` 的个位半字节是 `A`，不是合法 BCD 数字。UTC-5 的正确编码是 **`0x0A`**（十位 `2 | 0x08`、个位 `0`，低半字节在前）。
>
> `SmsPduParserTests` 新增 `ParsesServiceCentreTimestampWithSignedTimezone`：`0x23` → **+480 分钟**（UTC+8）、`0x0A` → **−300 分钟**（UTC−5）。

---

## 三、P2：中等风险与设计问题

**状态：19/20 已修复，1 项（P2-3）改为澄清注释。** 逐项修复记录见本节末尾。

| # | 位置 | 问题 | 影响与建议 |
|---|---|---|---|
| 1 | `AtEngine.cs:164` | `Encoding.UTF8.GetString(buffer, 0, read)` 按读取块解码 | 多字节字符跨块被截断成 U+FFFD（运营商名、UCS2 文本回显）。应使用**有状态** `Encoding.UTF8.GetDecoder()` 或按字节累积到行尾再解码 |
| 2 | `SmsPduParser.cs:221-228` | GSM7+UDH 分支用 `ud[0]`（此时已被切掉 UDHL，指向正文首字节）计算 `udhSeptets` | 该值为垃圾（常为负），`septetCount` 退化为 `data.Length*8/7`，末段会多解出填充位（尾部出现 `@`）。应改用 `DecodeUserData` 传入的原始 UDHL，并按 `UDHL*8` 的填充位做位移 |
| 3 | `DeviceInfoService.cs:205-218` | `Es10 = es10;` 之后 `finally { await es10.CloseAsync(); }` 必然执行 | 成功路径也把通道关了，与注释"保留供后续复用"相反；所有调用方都得靠 `Channel <= 0` 重新打开，多一次 CCHO/CCHC 往返 |
| 4 | `PhonebookService.cs:91-94` | `finally` 里用**已被取消的 `ct`** 恢复 `AT+CSCS` | 用户取消读电话簿后，模块会停留在 UCS2 字符集，影响后续文本类命令（SMS 走 PDU 不受影响）。恢复操作应使用 `CancellationToken.None` |
| 5 | `SerialAtChannel.cs:56-65` | `OpenAsync` 超时后主动放弃 `SerialPort`，注释明说"不 Dispose" | 后台线程随后仍可能 `Open()` 成功并永久持有句柄；每次失败连接泄漏一个 OS 句柄。应改为在 `OpenTimeout` 后后台 `Close/Dispose` 兜底 |
| 6 | `SerialAtChannel.cs:228、293`；`WindowsAtChannelProvider.cs:163` | `task.Wait(timeout)` / `task.Wait(5s)` 同步阻塞 | `EnumerateSerialPorts`、`GetPortFriendlyMap` 会被 UI 路径调用（`DescribeConnectionFailureAsync`、`IsBluetoothPort`），可能造成 3~5s 界面卡顿。应改为异步 + `CancellationToken` |
| 7 | `UsbAtChannel.cs:261-299` | 同步 `WinUsb_ReadPipe`（最长 500ms）在 `await _ioLock` 之后紧接执行 | 若调用链起始于 UI 线程（`AtEngine` 第一条命令常常如此），阻塞会落在 UI 线程上。建议整个读写在 `Task.Run` 中执行，或改用 `OVERLAPPED` + 事件 |
| 8 | `MainWindow.cs:225` + `SmsService.SyncStoredAsync` | 每 30s 全量 `AT+CMGL=4`，并重新解析所有短信 | 长期运行时产生持续 AT 流量与 CPU 开销（`AT+CMGL=4` 用 60s 超时）。建议仅在收到 `+CMTI` 时按索引读取（已有 `ReadByIndexAsync`），轮询仅做兜底并降低频率 |
| 9 | `PrioritizedCommandLock.cs:66-68` | `State == Granted` 分支在 `lock` 内 `GetAwaiter().GetResult()` | `Release()` 是先置 `State`、后 `TrySetResult`，存在"已授权但任务未完成"的窗口，此时会在持锁状态阻塞一个线程。应把租约放进 `Waiter` 字段，不依赖 TCS 的完成时机 |
| 10 | `DeviceOperationCoordinator.cs:198、294-296` | `CommitAudioPrepare` 里 `Dispose()` 掉 `_prepareCancellation`，而 `lease.OperationCancellationToken` 仍被外部持有；`DisposeAsync` 先 `Wait` 再 `Release` 再 `Dispose` | 前者若外部继续 `Register` 会抛 `ObjectDisposedException`；后者在有未释放租约时 `Release` 到已 Dispose 的信号量会抛 `ObjectDisposedException`。建议用引用计数或延迟释放 |
| 11 | `SettingsPage.cs:137-147` | `File.WriteAllText` 直接覆盖 `settings.json` | 掉电/崩溃会留下截断的 JSON（下次启动静默回落默认值，用户配置全丢）。应写临时文件 + `File.Replace`；另 webhook URL（常含 token）以明文存在 `%APPDATA%`，建议至少限制文件权限 |
| 12 | `LogicalChannelEs10.cs:432-452` | 用 eUICC 返回的 `Address` 直接构造 URL 并 POST | 恶意/异常卡片可把请求指向任意主机（SSRF）。建议白名单校验（GSMA 域名、HTTPS、禁止 IP/内网）并限制重定向 |
| 13 | `SmsForwarder.cs:17-67` | 所有收到的短信（含验证码）无条件转发到 Bark/飞书/钉钉 | 属于产品设计，但风险等级不低；建议增加"仅转发匹配规则"或至少默认关闭 + 首次启用确认 |
| 14 | `WindowsPlatformServices.cs:20` | `EnumerateKnownUsbDevices() => []` | Windows 上设备页 USB 摘要恒为空，与 Linux/macOS 行为不一致（`UsbAtChannel` 明明有能力枚举）。属未完成实现 |
| 15 | `PlatformAdapterFactory.cs:10-21` | 用字符串 `Type.GetType` + 反射创建平台实现 | 平台程序集未随包分发时报 `TypeLoadException`，信息不友好；且不利于裁剪/AOT。建议 `#if WINDOWS` 直接引用 + 编译期绑定，或给出明确的缺失提示 |
| 16 | `UsbAtChannel.cs:206-207` | `mi_02` 或 `mi_03` 都算 AT，取枚举到的第一个 | 部分固件 `MI_02` 是 AT、`MI_03` 是 modem/诊断口，可能连到错误接口。建议按固定优先级并校验 `bInterfaceClass`（0xFF vendor specific + subclass 0xFF） |
| 17 | `CusdService.cs:38-41` | `catch (Exception)` 把 `OperationCanceledException` 也吞成失败结果 | 与其它服务的取消语义不一致，页面关闭时的取消会显示成"发送异常" |
| 18 | `SmsService.cs:379-391`、`DeviceInfoService.cs:276-287` | `SafeAsync`/`TryAsync` 把 `ERROR`、超时、传输失败一律折成 `null` | 丢掉了 `AtCompletionStatus`，排障时无法区分"模块拒绝"和"没插卡"。建议返回 `AtResult?` 并在调用处按 `Completion` 分支 |
| 19 | `MacAtChannelProvider.cs:98-135`、`LinuxAtChannelProvider.cs:59-95` | 逐端口 2 次 × 700ms 顺序探测，全局限 15s | 端口多时后面的候选永远轮不到；且 `OpenAsync` 超时会泄漏句柄（同 P2-5）。建议对"已知模块端口"优先、其余并发且带上限 |
| 20 | `ModemManager.cs:199-215` | 端口在 `OpenAsync`/AT 探测**之前**就被写入 `_portHints` 并持久化 | 与注释"记住成功端口"不符。探测失败会把坏端口永久置顶，拖慢后续每次连接 |

### P2 逐项修复记录

| # | 状态 | 修复要点 |
|---|---|---|
| 1 | ✅ | `AtEngine` 改用有状态 `_decoder = Encoding.UTF8.GetDecoder()`，跨读取块保留多字节字符状态 |
| 2 | ✅ | `DecodeUserData` 显式接收原始 `udhOctets`（按字节而非 `ud[0]`），GSM7 以 `startSeptet: udhSeptets` 起解，尾部填充位不再被解成 `@` |
| 3 | 🟡 澄清注释 | 未按建议改行为，理由见下 |
| 4 | ✅ | `finally` 中恢复 `AT+CSCS` 改用 `CancellationToken.None` |
| 5 | ✅ | `DisposeWhenOpened(openTask, port)` 在后台打开任务成功或失败后都关闭并释放端口；正常完成但打开失败时也立即释放端口对象 |
| 6 | ✅ | 新增 `EnumerateCandidatesAsync(TimeSpan?, CancellationToken)`；`WindowsAtChannelProvider` 改用 `GetPortFriendlyMapAsync` + `SemaphoreSlim PortMapGate`，`task.Wait` 全部移除 |
| 7 | ✅ | `ReadAsync` / `WriteAsync` 的 `WinUsb_ReadPipe` / `WritePipe` P/Invoke 包进 `Task.Run`，`await _ioLock` 之后不再有同步阻塞段 |
| 8 | ✅ | `_smsPollTimer` 由 30 秒改为 5 分钟，注释说明 `+CMTI` 为主路径、轮询仅作兜底 |
| 9 | ✅ | `Waiter` 增加 `Lease` 字段；`Release()` 先赋 `next.Lease` 再置 `Granted`；`AbandonOrTakeGranted` 直接返回该租约，不再在 `lock` 内等 TCS |
| 10 | ✅ | Prepare 租约持有 CTS；未提交源在租约归还时释放，已提交源在确认释放音频会话后释放；`DisposeAsync` 检查 `WaitAsync(2s)` 的布尔结果，超时后等活动租约归还再清理信号量 |
| 11 | ✅ | `SaveToPath` 用进程内锁、临时文件与 `File.Replace`（不支持时回退 `File.Move(overwrite: true)`）；Unix 权限设为 `0600`，权限失败时返回保存失败并清理临时文件 |
| 12 | ✅ | `NotificationEndpointPolicy` 限制 HTTPS 与公网 DNS 目标；默认 handler 每次新连接重新解析、拒绝非公网答案并直连已验证 IP，关闭代理和重定向。自定义注入的 `HttpClient` 由注入方负责传输层策略 |
| 13 | ✅ | 新增 `SmsForwardingEnabled`（**默认 `false`**）与 `HasForwardingTarget`；设置页加开关与风险提示；`SmsForwarder.ForwardAsync` 入口二次校验；`OnSettingsChanged` 即时重订阅 |
| 14 | ✅ | 改为基于 `WindowsUsbDeviceEnumerator.EnumeratePresent()` + `DescribeKnownModules` + `MatchesModule`，与 Linux/macOS 行为对齐 |
| 15 | ✅ | `Type.GetType(..., throwOnError: false)`，捕获 `FileLoadException` / `FileNotFoundException` / `BadImageFormatException` 并转成带平台名的 `InvalidOperationException`；新增 `DescribePlatform()` |
| 16 | ✅ | `FindAtInterfacePaths()` 按 MI_02 → MI_03 排序；`TryOpenInterface` 校验接口描述符；USB 通道首次 AT 探测失败时再回退到串口探测 |
| 17 | ✅ | `catch (OperationCanceledException) { throw; }` 置于通用 catch 之前 |
| 18 | ✅ | `SmsService`、`DeviceInfoService`、`PhonebookService` 保留失败的 `AtResult`；`Error` 记 Info/Warn，超时/传输失败带命令名记录，取消时重抛，调用处只解析成功响应 |
| 19 | ✅ | 新增 `AtPortProbe.FindAsync(create, ordered, priorityCount, budget, concurrency)`：前 `priorityCount` 个**串行**探测（命中即止、不打开其它端口），其余分批并发兜底；`ProbeAsync` 保证通道释放。macOS/Linux 全部接入 |
| 20 | ✅ | 新增 `RememberSuccessfulPort(string)`，仅在 AT 探测成功后调用，坏端口不再被永久置顶 |

### P2-3 未采纳说明：`Es10` 通道保留

建议为「保留通道供后续复用，省掉一次 CCHO/CCHC 往返」，实际**未改行为**，原因：`tests/CellPort.App.Tests/EsimPageTests.cs` 中的 `RenameOpensIsdrChannelAfterProfileReadClosedIt` 明确断言——读取 profile 后通道已关闭，重命名时必须重新打开 ISDR 通道。这是为规避 UICC refresh 后残留的失效通道句柄，属于刻意设计而非缺陷。

处理方式：保留现有行为，把容易误导的「保留供后续复用」注释改为准确描述（探测后关闭、按需重开，以及这样做的原因）。

---

## 四、P3：工程化与代码质量

**状态：8 项均未作为独立任务处理**（其中 2 个细项已随 P2 顺带解决，见下）。建议单独开一次「工程化清理」提交，避免与功能修复混在一起。

1. **缺少统一的编译约束**：仓库没有 `Directory.Build.props`，未开启 `TreatWarningsAsErrors`、`AnalysisLevel`、`EnableNETAnalyzers`、可空引用已是 `enable` 但很多`!`仍在（`EsimPage.cs:118-121`）。建议集中配置并逐步收紧。
2. **静默吞异常过多**：`ModemManager.RefreshStatusAsync`、`DisconnectAsync`、`SmsService.SafeAsync`、`PhonebookService.ExecAsync` 等大量 `catch { }`，出问题时日志里只有"失败"。建议至少 `AppLog.Warn` 一行带命令名与 `Completion`。（P2-18 涉及的 `SmsService` / `DeviceInfoService` / `PhonebookService` 已补上，其余待清理）
3. **死代码 / 注释与实现不符**：
   - ~~`SmsPduParser.cs:91-94` `if (smscLen == 0) reader.Skip(0);` 无意义~~ ✅ 已随 P2-2 删除；
   - `SmsService.HandleUnsolicited`（`:106-123`）实际从未被调用（泵走 `FeedLine`），两条路径逻辑重复；
   - `SerialAtChannel.WriteTimeout`（`:135`）声明后未使用；
   - `AtEngine.cs:137` `lastText` 只在 trace 中使用，命名易误解；
   - ~~`DeviceInfoService.Es10` 注释与 P2-3 的实际行为相反~~ ✅ 已随 P2-3 改正注释。
4. **`async void` 事件处理器**：`MainWindow.OnSmsReceived`（`:314`）。异常虽被包住，但仍建议改 `async Task` + `_ = HandleAsync()` 模式，避免未观测异常直接终止进程。
5. **注释语言混杂**：同一个文件里中英文注释交替（`ModemManager`、`CallService`、`ClirrCallList` 尤其明显），建议统一（对外文档中文，代码注释英文或反之）。
6. **UI 层手工构建控件**：`MainWindow.cs` 371 行里大部分是 `new Grid { ... }`，`DevicePage`/`SmsPage` 类似，逻辑与布局强耦合，测试只能靠 `Name` 查找。可考虑抽 XAML/AXAML 或至少抽成 Builder 类。
7. **控制台 AT 白名单偏窄**：`ModemManager.ClassifyAtCommand`（`:686-710`）只拦截 `CFUN/CGATT/QNETDEVCTL/QCFG USBNET`，`AT+EGMR=1,7,"..."`（改写 IMEI）、`AT+QCFG="usbnet"`（部分写法不带引号即绕过）、`AT+QRST`（重启）等破坏性命令会落到 `RawConsoleCommand`，仅在音频会话占用时才被拦。建议改为"默认可写、显式黑名单 + 二次确认"，并在 UI 上标注高危命令。
8. **正则解析偏脆弱**：`SyncStoredAsync` 的 `+CMGL` 正则假定第三字段无引号逗号（`:62-64`）；`ParseCnum`/`ParseReg` 依赖 `RawText` 子串匹配。建议对 SMSC/alpha 字段做完整的引号感知切分（可复用 `ClccCallListParser.TrySplitFields` 的思路）。

---

## 五、修复执行顺序（已完成 P0~P2）

实际按下列顺序执行，P3 暂缓：

1. **立刻** ✅：P0-1、P0-2、P0-3（短信发送正确性）——并同步修正 `SmsCodecTests` 中被固化的错误期望。执行中额外发现并修复 **P0-5（TP-MR 缺失）**，否则前三条修好后短信依然发不出去。
2. **本迭代** ✅：P1-1（通道泄漏，直接影响可用性）、P1-2（NRE）、P1-3（断开清理）、P1-5（时间戳）。
3. **计划内** ✅：P1-4（CMGS 提示符）、P2-5/6/7（UI 线程阻塞与句柄泄漏）、P2-11（配置原子写入）；其余 P2 一并完成。
4. **持续改进** ⬜：P3 的编译约束、日志、死代码清理；`CallTracker`/`ClccCallList` 这部分的写法建议作为其余模块的模板。

改动规模：此处记录首轮 P0–P2 修复时的统计（31 个文件修改，新增 2 个生产文件与 2 个测试文件，`+1456 / -436` 行）；不含本轮补充修正。

---

## 六、值得肯定的设计

- `CallTracker` + `CallSnapshot` 用不可变快照表达"未知 ≠ 空闲"，`TryApply` 用 module generation 拒绝跨会话的陈旧结果——这类状态机在同类项目里很少见。
- `ClccCallListParser` 对部分响应/重复索引/格式错误的处理是"保守失败"而非"猜测成功"，方向正确。
- `AudioFrameBuffer` 的 SPSC 无锁环 + `Read` 零填充、`AudioFormatConverter` 跨回调保留滤波状态与小数相位，实现质量较高。
- `RuntimeManifestValidator` 对路径穿越、重解析点（符号链接）、大小/摘要校验的防护相当完整（`CopyAndVerify` 边拷边算 SHA-256 且校验总长）。
- `AdbTransportClient` 只接受显式 `-t <transport_id>` 且执行前校验 `IsCurrent`，避免了 adb 多设备误操作；`ProcessStartInfo.ArgumentList` 也规避了参数注入。

---

## 七、验证记录

> 以下是首轮修复完成时记录的数据。本轮追加修正没有重新执行构建或测试，不能据此认定追加修正确已通过验证。

**编译**

```
dotnet build CellPort.slnx      0 警告，0 错误
```

**测试**

| 项目 | 结果 |
|---|---|
| `CellPort.Core.Tests` | 146 通过 / 0 失败 |
| `CellPort.Audio.Core.Tests` | 39 通过 / 0 失败 |
| `CellPort.App.Tests` | 37 通过 / 0 失败 |
| `CellPort.Platform.MacOS.Tests` | 18 通过 / 0 失败 |
| `CellPort.Platform.Linux.Tests` | 6 通过 / 0 失败 |
| `CellPort.Platform.Windows.Tests` | 编译通过（当前主机 macOS，Windows 专属用例未执行） |

**新增与重写的测试**

| 文件 | 覆盖内容 |
|---|---|
| `tests/CellPort.Core.Tests/SmsSendPipelineTests.cs`（新增 148 行） | CMGS 提示符不发 5 秒等待、长中文分两段、CMGS 被拒时不写 PDU、`Prompt` 完成状态 |
| `tests/CellPort.Core.Tests/NotificationEndpointPolicyTests.cs`（新增 61 行） | 主机名白名单、IPv4/IPv6 私有/公网分类 |
| `tests/CellPort.Core.Tests/SmsCodecTests.cs`（+148 行） | 剔除被固化的错误期望；BCD 边界、扩展表字符、长短信分段往返、UDL 偏移校验 |
| `tests/CellPort.Core.Tests/SmsPduParserTests.cs`（+19 行） | 正/负时区解析向量 |

**新增的生产代码**

| 文件 | 行数 | 用途 |
|---|---|---|
| `src/CellPort.Core/Services/NotificationEndpointPolicy.cs` | 166 | SSRF 防护：URL 校验 + 主机名白名单 + IP 地址分类（P2-12） |
| `src/CellPort.Transport.Serial/AtPortProbe.cs` | 150 | 端口探测调度：优先段串行 + 其余并发限流（P2-19） |

---

## 八、复核中沉淀的协议约定

以下几条在修改 `SmsCodec` / `SmsPduParser` 前必须先确认，避免再次踩坑：

- TP-DA 的 BCD **不含任何前缀**，国际格式只由 TOA `0x91` 表达。
- TP-UDL 语义随 DCS 变化：GSM7 是 **septet 数**（含 UDH 的 7），UCS2 是**字节数**（含 UDH 的 6）。
- SMS-SUBMIT 字段序：`SMSC长度(00) → TP-FO(11/51) → TP-MR(00) → DA长度 → TOA → BCD → PID(00) → DCS → VP(8F，12小时) → UDL → UD`。
- 时区符号位是 `raw[6]` 的 **bit3（`0x08`）**，且必须取原始字节，不能读 `Dec()` 后的十进制值。
- GSM7 单段 160 septet / 带 UDH 153；UCS2 单段 140 字节 / 带 UDH 134。

复用建议：`AtEngine` 的 `expectPrompt` 机制把「中间态提示符」变成一等公民，后续 `AT+CUSD` 的 `+CUSD:` 中间响应、`AT+CMGL` 分页都可以复用同一套逻辑；`SmsCodec` 的分段策略全部收在 `BuildSubmitPdus` 内部，将来要支持 16-bit 参考号（`IEI 0x08`）只需改 `BuildConcatUdh`。


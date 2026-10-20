using System.Diagnostics;
using System.Text;
using CellPort.Core.At;
using CellPort.Core.Services;
using CellPort.Core.Transport;
using Xunit;

namespace CellPort.Core.Tests;

/// <summary>
/// 短信发送链路（SmsService → AtEngine → 通道）的端到端测试：
/// 覆盖 PDU 分段、CMGS ">" 提示符判定与失败中止。
/// </summary>
public sealed class SmsSendPipelineTests
{
    [Fact]
    public async Task SendAsync_UsesCmgsPromptInsteadOfWaitingForOk()
    {
        await using var channel = new SmsSubmitChannel();
        await using var engine = new AtEngine(channel);
        var service = new SmsService(engine);

        var watch = Stopwatch.StartNew();
        var ok = await service.SendAsync("+8613800138000", "Test");
        watch.Stop();

        Assert.True(ok);
        // CMGS 只回 ">"：若不按提示符判定，这里会固定等满 5 秒超时。
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"发送耗时 {watch.Elapsed}，疑似等待 OK 超时。");

        var pdu = Assert.Single(channel.SubmittedPdus);
        var parsed = SmsPduParser.Parse(pdu);
        Assert.NotNull(parsed);
        Assert.Equal("+8613800138000", parsed.Number);
        Assert.Equal("Test", parsed.Body);

        var message = Assert.Single(service.Inbox);
        Assert.Equal(SmsDirection.Outgoing, message.Direction);
    }

    [Fact]
    public async Task SendAsync_LongChineseBodySubmitsEverySegment()
    {
        await using var channel = new SmsSubmitChannel();
        await using var engine = new AtEngine(channel);
        var service = new SmsService(engine);

        var body = string.Concat(Enumerable.Repeat("验证码 123456。", 12)); // 96 字符 > 70
        var ok = await service.SendAsync("13800138000", body);

        Assert.True(ok);
        Assert.Equal(2, channel.SubmittedPdus.Count);
        Assert.Equal(2, channel.CmgsRequests.Count);
        Assert.All(channel.CmgsRequests, length => Assert.True(length <= 160, $"TPDU 长度 {length} 超出上限。"));

        var bodies = channel.SubmittedPdus
            .Select(SmsPduParser.Parse)
            .Select(m => m!.Body);
        Assert.Equal(body, string.Concat(bodies));
    }

    [Fact]
    public async Task SendAsync_AbortsWhenCmgsIsRejected()
    {
        await using var channel = new SmsSubmitChannel { RejectCmgs = true };
        await using var engine = new AtEngine(channel);
        var service = new SmsService(engine);

        var ok = await service.SendAsync("+8613800138000", "Test");

        Assert.False(ok);
        // CMGS 被拒时绝不能把正文当 AT 命令行写下去。
        Assert.Empty(channel.SubmittedPdus);
        Assert.Empty(service.Inbox);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsPromptCompletion()
    {
        await using var channel = new SmsSubmitChannel();
        await using var engine = new AtEngine(channel);

        var result = await engine.ExecuteAsync("AT+CMGS=20", TimeSpan.FromSeconds(2),
            expectPrompt: ">");

        Assert.True(result.Succeeded);
        Assert.Equal(AtCompletionStatus.Prompt, result.Completion);
    }

    /// <summary>模拟模块：CMGS 返回 "&gt;" 提示符（不带 OK），PDU 写完后才回 OK。</summary>
    private sealed class SmsSubmitChannel : IAtChannel
    {
        private byte[] _reply = [];
        public List<string> SubmittedPdus { get; } = [];
        public List<int> CmgsRequests { get; } = [];
        public bool RejectCmgs { get; init; }

        public string Description => "SMS submit test channel";
        public AtChannelKind Kind => AtChannelKind.SerialPort;
        public bool IsOpen { get; private set; }

        public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var command = Encoding.ASCII.GetString(data.Span).TrimEnd('\r');

            if (command.EndsWith('\u001A'))
            {
                SubmittedPdus.Add(command.TrimEnd('\u001A'));
                _reply = Encoding.ASCII.GetBytes("\r\n+CMGS: 7\r\nOK\r\n");
                return Task.CompletedTask;
            }

            if (command.StartsWith("AT+CMGS=", StringComparison.Ordinal))
            {
                if (int.TryParse(command["AT+CMGS=".Length..], out var length))
                {
                    CmgsRequests.Add(length);
                }

                _reply = RejectCmgs
                    ? Encoding.ASCII.GetBytes($"\r\n{command}\r\n+CMS ERROR: 304\r\n")
                    : Encoding.ASCII.GetBytes($"\r\n{command}\r\n> ");
                return Task.CompletedTask;
            }

            _reply = Encoding.ASCII.GetBytes($"\r\n{command}\r\nOK\r\n");
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct = default)
        {
            var size = Math.Min(buffer.Length, _reply.Length);
            if (size == 0)
            {
                return Task.FromResult(0);
            }

            _reply.AsMemory(0, size).CopyTo(buffer);
            _reply = _reply[size..];
            return Task.FromResult(size);
        }

        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}

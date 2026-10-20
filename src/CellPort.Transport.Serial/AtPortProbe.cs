using System.Diagnostics;
using System.Text;
using CellPort.Core.Transport;

namespace CellPort.Transport.Serial;

/// <summary>
/// 串口 AT 探测：高优先级端口逐个探测（命中即返回），其余候选分批并发兜底。
///
/// 逐端口串行探测在候选较多时会让排在后面的端口永远轮不到（每个端口最长 2×700ms，
/// 总预算 15 秒只够约 10 个端口）；全部并发又会同时抓住多个串口句柄。
/// 因此把「已知模块端口/上次成功的端口」放在串行段，其余候选按批次并发探测，
/// 既保持优先级语义，又保证每个候选都有机会被探测到。
/// </summary>
public static class AtPortProbe
{
    /// <summary>默认并发探测数。</summary>
    public const int DefaultConcurrency = 3;

    /// <summary>
    /// 按顺序查找 AT 端口。
    /// </summary>
    /// <param name="create">根据端口名创建通道的工厂。</param>
    /// <param name="ordered">按优先级排序的候选端口。</param>
    /// <param name="priorityCount">
    /// 前 N 个端口属于高优先级（串行探测，命中即返回）；其余端口并发兜底。
    /// </param>
    /// <param name="budget">总时间预算。</param>
    /// <param name="concurrency">兜底段的并发数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的端口名；未找到返回 null。</returns>
    public static async Task<string?> FindAsync(
        Func<string, IAtChannel> create,
        IReadOnlyList<string> ordered,
        int priorityCount,
        TimeSpan budget,
        int concurrency = DefaultConcurrency,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(ordered);
        if (ordered.Count == 0)
        {
            return null;
        }

        var serialCount = Math.Clamp(priorityCount, 0, ordered.Count);
        var batchSize = Math.Max(1, concurrency);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);

        try
        {
            // 第 1 段：高优先级端口逐个探测，命中即返回（不打开任何多余句柄）。
            for (var i = 0; i < serialCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested)
                {
                    return null;
                }

                if (await ProbeAsync(create, ordered[i], timeout.Token).ConfigureAwait(false))
                {
                    return ordered[i];
                }
            }

            // 第 2 段：其余候选分批并发，批内仍按原顺序取第一个命中者。
            for (var start = serialCount; start < ordered.Count; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested)
                {
                    return null;
                }

                var batch = new string[Math.Min(batchSize, ordered.Count - start)];
                for (var i = 0; i < batch.Length; i++)
                {
                    batch[i] = ordered[start + i];
                }

                var results = await Task.WhenAll(
                    batch.Select(port => ProbeAsync(create, port, timeout.Token))).ConfigureAwait(false);

                for (var i = 0; i < results.Length; i++)
                {
                    if (results[i])
                    {
                        return batch[i];
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 总预算用尽：调用方按「未找到」处理。
            return null;
        }

        return null;
    }

    /// <summary>探测单个端口是否能响应 AT。</summary>
    public static async Task<bool> ProbeAsync(
        Func<string, IAtChannel> create, string port, CancellationToken ct)
    {
        try
        {
            // 探测用的通道必须在返回前释放，避免与随后的真实连接争用端口。
            await using var channel = create(port);
            await channel.OpenAsync(ct).ConfigureAwait(false);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                await channel.WriteAsync("AT\r"u8.ToArray(), ct).ConfigureAwait(false);

                var response = new StringBuilder();
                var buffer = new byte[256];
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromMilliseconds(700))
                {
                    ct.ThrowIfCancellationRequested();
                    var count = await channel.ReadAsync(buffer, TimeSpan.FromMilliseconds(150), ct)
                        .ConfigureAwait(false);
                    response.Append(Encoding.ASCII.GetString(buffer, 0, count));

                    if (response.ToString()
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Any(line => line.Trim() == "OK"))
                    {
                        return true;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException
                                  or InvalidOperationException)
        {
            // 被占用、已拔出或不是 AT 口：不影响其它候选。
        }

        return false;
    }
}

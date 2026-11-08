using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PptEase.Models;

namespace PptEase;
public sealed class HostClient
{
    private readonly string pipePrefix;
    private readonly int? targetPid;
    public HostClient(string pipePrefix = "PptEase", int? targetPid = null) { this.pipePrefix = pipePrefix; this.targetPid = targetPid; }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public async Task<JsonElement> RequestAsync(string op, object? payload = null, int timeoutMs = 20000)
    {
        var processes = Process.GetProcessesByName("POWERPNT");
        int[] processIds;
        try { processIds = processes.Where(p => targetPid == null || p.Id == targetPid).Select(p => p.Id).OrderBy(id => id).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
        if (processIds.Length == 0) throw new InvalidOperationException("请先打开 PowerPoint，再加载增强。");
        Exception? last = null;
        foreach (var processId in processIds)
        {
            using var cancellation = new CancellationTokenSource(timeoutMs);
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipePrefix + "-" + processId, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(500, cancellation.Token);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var id = Guid.NewGuid().ToString("N");
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { op, id, payload = payload ?? new { } }));
                var line = await reader.ReadLineAsync(cancellation.Token);
                if (line == null) throw new IOException("PowerPoint 增强连接已关闭，请刷新。");
                using var doc = JsonDocument.Parse(line);
                var response = doc.RootElement;
                if (response.TryGetProperty("id", out var replyId) && replyId.GetString() != id) throw new IOException("响应标识不匹配，请重新连接。");
                if (!response.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(response.TryGetProperty("error", out var error) ? error.GetString() : "PowerPoint 操作失败。");
                return response.GetProperty("result").Clone();
            }
            catch (TimeoutException ex) { last = ex; }
            catch (IOException ex) { last = ex; }
        }
        throw new InvalidOperationException(IntegrationInstaller.UnloadedGuidance(), last);
    }
    public async Task<HostSnapshot> RefreshAsync()
    {
        try { return (await RequestAsync("status")).Deserialize<HostSnapshot>(JsonOptions) ?? new HostSnapshot(); }
        catch (Exception ex)
        {
            return new HostSnapshot { Connected = Process.GetProcessesByName("POWERPNT").Length > 0, IntegrationLoaded = false, Status = ex.Message };
        }
    }
    public async Task<HostSnapshot> SetLockedAsync(IEnumerable<ShapeItem> items, bool locked)
    {
        var selected = items.ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("请先选择对象。");
        var result = await RequestAsync(locked ? "lock" : "unlock", new { keys = selected.Select(s => s.Key).ToArray() });
        return result.Deserialize<HostSnapshot>(JsonOptions) ?? await RefreshAsync();
    }
    public async Task SelectAsync(ShapeItem item) => await RequestAsync("select", new { key = item.Key });
}

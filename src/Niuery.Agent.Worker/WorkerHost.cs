using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Niuery.Agent.Runtime.Maf;

namespace Niuery.Agent.Worker;

public sealed class WorkerHost(Store store, IReadOnlyList<ProviderConfiguration> providers) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cancellation, Task Work)> active = new();
    private ApprovalGate? approvalGate;
    private ApprovalGate Approvals => approvalGate ??= new ApprovalGate((id, type, body) => Emit(id, type, body));
    public Task<object> Handle(string method, JsonElement payload)
    {
        object result = method switch
        {
            "hello" => new { version = 1, name = "Niuery Agent", capabilities = new[] { "streaming", "cancel", "history" } },
            "providers.list" => providers.Select(p => new { p.Id, p.Kind, p.Model, p.SupportsTools, p.SupportsStreaming }).ToArray(),
            "history.list" => store.History(),
            "events.list" => store.Events(payload.Required("runId"), payload.TryGetProperty("afterSequence", out var after) ? after.GetInt64() : 0),
            "run.start" => Start(payload),
            "run.cancel" => Cancel(payload.Required("runId")),
            "approval.respond" => Approvals.Respond(payload.Required("runId"), payload.Required("approvalId"), payload.Required("digest"), payload.GetProperty("approved").GetBoolean()),
            "workspace.diff" => store.Events(payload.Required("runId")).Where(e => e.Type == "artifact.created").Select(e => e.Payload).ToArray(),
            "worker.shutdown" => new { stopping = true },
            _ => throw new InvalidOperationException("不支持的协议方法。")
        };
        return Task.FromResult(result);
    }
    private object Cancel(string id)
    {
        if (active.TryGetValue(id, out var run)) run.Cancellation.Cancel();
        return new { accepted = true };
    }
    private object Start(JsonElement payload)
    {
        if (!active.IsEmpty) throw new InvalidOperationException("首版本地执行器一次只运行一个任务，请等待或取消当前执行。");
        var workspace = Path.GetFullPath(payload.Required("workspace"));
        if (!Directory.Exists(workspace)) throw new InvalidOperationException("工作区目录不存在。");
        var provider = providers.SingleOrDefault(p => p.Id == payload.Required("providerId")) ?? throw new InvalidOperationException("提供商不存在。");
        provider.Validate(); provider.ResolveApiKey();
        var prompt = payload.Required("prompt");
        if (prompt.Length > 32000) throw new InvalidOperationException("任务说明过长，请控制在 32000 字符内。");
        var row = store.Create(workspace, prompt, provider.Id);
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(provider.TimeoutSeconds));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () => { await ready.Task; await Run(row, provider, cancellation.Token); });
        active[row.Id] = (cancellation, task); ready.SetResult();
        return row;
    }
    private void Emit(string runId, string type, object payload, string? status = null)
    {
        var item = store.Append(runId, type, payload, status);
        Wire.Send(new { version = 1, eventData = item });
    }
    private async Task Run(RunRow row, ProviderConfiguration provider, CancellationToken token)
    {
        var status = "completed";
        var message = "执行完成。";
        try
        {
            Emit(row.Id, "run.started", new { message = "执行已开始。", provider.Model });
            using var client = HarnessFactory.CreateClient(provider);
            var tools = new WorkspaceTools(row.Workspace, (name, arguments, ct) => Approvals.Require(row.Id, name, arguments, ct),
                (type, body) => Emit(row.Id, type, body), token);
            var agent = HarnessFactory.CreateCoding(client, tools.Functions());
            var session = await agent.CreateSessionAsync(token);
            var pending = new StringBuilder();
            await foreach (var update in agent.RunStreamingAsync(row.Prompt, session, cancellationToken: token))
            {
                token.ThrowIfCancellationRequested(); pending.Append(update.Text);
                if (pending.Length >= 80) { Emit(row.Id, "message.delta", new { text = pending.ToString() }); pending.Clear(); }
            }
            if (pending.Length > 0) Emit(row.Id, "message.delta", new { text = pending.ToString() });
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { status = "cancelled"; message = "执行已取消或超时。"; }
        catch (Exception) { status = "failed"; message = "执行失败，请检查模型服务和配置。"; }
        finally
        {
            if (active.TryRemove(row.Id, out var run)) run.Cancellation.Dispose();
            Emit(row.Id, "run." + status, new { message }, status);
        }
    }
    public async ValueTask DisposeAsync()
    {
        var runs = active.Values.ToArray();
        foreach (var run in runs) run.Cancellation.Cancel();
        await Task.WhenAll(runs.Select(r => r.Work));
    }
}

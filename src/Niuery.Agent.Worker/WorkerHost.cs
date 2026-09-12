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
    public async Task<object> Handle(string method, JsonElement payload)
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
            "workspace.read" => await ReadWorkspace(payload),
            "workspace.apply" => BeginApply(payload),
            "workspace.command" => BeginCommand(payload),
            "artifact.undo" => await Undo(payload),
            "worktree.create" => new { path = await WorktreeManager.CreateAsync(payload.Required("repository"), payload.Required("name"), CancellationToken.None) },
            "worktree.remove" => await RemoveWorktree(payload),
            "worker.shutdown" => new { stopping = true },
            _ => throw new InvalidOperationException("不支持的协议方法。")
        };
        return result;
    }
    private static async Task<object> RemoveWorktree(JsonElement payload)
    { await WorktreeManager.RemoveAsync(payload.Required("repository"), payload.Required("path"), CancellationToken.None); return new { removed = true }; }
    private object Cancel(string id)
    {
        if (active.TryGetValue(id, out var run)) run.Cancellation.Cancel();
        return new { accepted = true };
    }
    private static async Task<object> ReadWorkspace(JsonElement payload)
    {
        var root = Path.GetFullPath(payload.Required("workspace"));
        var tools = new WorkspaceTools(root, (_, _, _) => Task.CompletedTask, (_, _) => { }, CancellationToken.None);
        return await tools.ReadFile(payload.Required("path"), payload.TryGetProperty("startLine", out var start) ? start.GetInt32() : 1, payload.TryGetProperty("lineCount", out var count) ? count.GetInt32() : 200);
    }
    private async Task<object> ApplyWorkspace(JsonElement payload)
    {
        var root = Path.GetFullPath(payload.Required("workspace"));
        var tools = new WorkspaceTools(root, (name, args, ct) => Approvals.Require(payload.Required("runId"), name, args, ct), (type, body) => Emit(payload.Required("runId"), type, body), CancellationToken.None);
        return await tools.ApplyPatch(payload.Required("path"), payload.Required("expectedHash"), payload.Required("oldText"), payload.Required("newText"));
    }
    private object BeginApply(JsonElement payload)
    {
        var copy = payload.Clone();
        _ = Task.Run(async () =>
        {
            try { await ApplyWorkspace(copy); }
            catch (Exception) { Emit(copy.Required("runId"), "operation.failed", new { message = "补丁执行失败。" }); }
        });
        return new { accepted = true };
    }
    private object BeginCommand(JsonElement payload)
    {
        var copy = payload.Clone();
        _ = Task.Run(async () =>
        {
            var runId = copy.Required("runId");
            try
            {
                var root = Path.GetFullPath(copy.Required("workspace"));
                var executable = copy.Required("executable");
                var arguments = copy.GetProperty("arguments").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                await Approvals.Require(runId, "运行命令", new { executable, arguments, root }, CancellationToken.None);
                var result = await WorkspaceTools.Execute(executable, arguments, root, CancellationToken.None);
                Emit(runId, "command.completed", new { result.ExitCode, result.Output, result.Truncated, message = $"命令完成，退出码 {result.ExitCode}" });
            }
            catch (Exception) { Emit(runId, "command.failed", new { message = "命令执行失败或被拒绝。" }); }
        });
        return new { accepted = true };
    }
    private async Task<object> Undo(JsonElement payload)
    {
        var artifact = store.Events(payload.Required("runId")).Where(e => e.Type == "artifact.created").Select(e => e.Payload).SingleOrDefault(e => e.GetProperty("artifactId").GetString() == payload.Required("artifactId"));
        if (artifact.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("找不到指定修改制品。");
        await UndoService.UndoAsync(artifact, CancellationToken.None); return new { undone = true };
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
            Emit(row.Id, "run.started", new { message = "执行已开始。", provider.Model, processId = Environment.ProcessId, startedAt = DateTimeOffset.UtcNow });
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

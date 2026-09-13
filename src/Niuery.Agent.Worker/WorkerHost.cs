using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Niuery.Agent.Runtime.Maf;
using Microsoft.Extensions.AI;

namespace Niuery.Agent.Worker;

public sealed class WorkerHost(Store store, IReadOnlyList<ProviderConfiguration> initialProviders, string configPath) : IAsyncDisposable
{
    private IReadOnlyList<ProviderConfiguration> providers = initialProviders;
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cancellation, Task Work)> active = new();
    private ApprovalGate? approvalGate;
    private ApprovalGate Approvals => approvalGate ??= new ApprovalGate((id, type, body) => Emit(id, type, body));
    public async Task<object> Handle(string method, JsonElement payload)
    {
        object result = method switch
        {
            "hello" => new { version = 1, name = "Niuery Agent", capabilities = new[] { "streaming", "cancel", "history", "project.open", "chat.start", "workspace.diff" } },
            "project.open" => await OpenProject(payload),
            "providers.list" => providers.Select(p => new { p.Id, p.Kind, p.BaseUrl, p.Model, Models = p.AvailableModels, p.ApiKey, p.SupportsTools, p.SupportsStreaming, p.TimeoutSeconds, p.Enabled }).ToArray(),
            "providers.save" => await SaveProviders(payload),
            "providers.test" => await TestProvider(payload),
            "providers.sync" => await SyncProvider(payload),
            "history.list" => store.History(payload.TryGetProperty("limit", out var historyLimit) ? historyLimit.GetInt32() : 200, payload.TryGetProperty("offset", out var historyOffset) ? historyOffset.GetInt32() : 0),
            "events.list" => store.Events(payload.Required("runId"), payload.TryGetProperty("afterSequence", out var after) ? after.GetInt64() : 0, payload.TryGetProperty("limit", out var eventLimit) ? eventLimit.GetInt32() : 1000),
            "run.start" => Start(payload),
            "chat.start" => StartChat(payload),
            "task.delete" => DeleteTask(payload.Required("taskId")),
            "run.continue" => Continue(payload),
            "run.cancel" => Cancel(payload.Required("runId")),
            "approval.respond" => Approvals.Respond(payload.Required("runId"), payload.Required("approvalId"), payload.Required("digest"), payload.GetProperty("approved").GetBoolean()),
            "workspace.diff" => GetWorkspaceDiff(payload),
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
    private async Task<object> SaveProviders(JsonElement payload)
    {
        var items = JsonSerializer.Deserialize<List<ProviderConfiguration>>(payload.GetProperty("providers"), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("提供商列表为空。");
        if (!items.Any(p => p.Enabled)) throw new InvalidOperationException("至少需要启用一个模型服务。");
        foreach (var p in items.Where(p => p.Enabled)) p.Validate();
        var temp = configPath + ".tmp"; await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new { providers = items }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })); File.Move(temp, configPath, true);
        providers = items;
        return new { saved = items.Count };
    }
    private static async Task<object> TestProvider(JsonElement payload)
    {
        var p = JsonSerializer.Deserialize<ProviderConfiguration>(
            payload.GetProperty("provider"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("提供商参数无效。");

        // 校验连接只需要确认地址和凭证可用，模型可以留空，方便先完成服务配置。
        p.Validate(false);
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Min(p.TimeoutSeconds, 15))
        };

        if (p.Kind == "openai-compatible")
        {
            var apiKey = p.ResolveApiKey(false);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        var url = new Uri(new Uri(p.BaseUrl.EndsWith('/') ? p.BaseUrl : p.BaseUrl + "/"), "models");
        try
        {
            var response = await client.GetAsync(url);
            return new
            {
                ok = response.IsSuccessStatusCode,
                status = (int)response.StatusCode,
                message = response.IsSuccessStatusCode
                    ? "连接成功。"
                    : $"服务返回错误（HTTP {(int)response.StatusCode}）。"
            };
        }
        catch (TaskCanceledException)
        {
            return new { ok = false, status = 0, message = "连接超时，请检查地址和网络。" };
        }
        catch (HttpRequestException)
        {
            return new { ok = false, status = 0, message = "连接失败，请检查地址、网络和服务状态。" };
        }
    }
    private static async Task<object> SyncProvider(JsonElement payload)
    {
        var p = JsonSerializer.Deserialize<ProviderConfiguration>(payload.GetProperty("provider"), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("提供商参数无效。"); p.Validate(false); using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Min(p.TimeoutSeconds, 15)) }; var apiKey = p.Kind == "ollama" ? "ollama" : p.ResolveApiKey(false); if (p.Kind == "openai-compatible") client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey); var url = new Uri(new Uri(p.BaseUrl.EndsWith('/') ? p.BaseUrl : p.BaseUrl + "/"), "models"); var json = await client.GetStringAsync(url); using var doc = JsonDocument.Parse(json); var models = doc.RootElement.TryGetProperty("data", out var data) ? data.EnumerateArray().Select(x => x.TryGetProperty("id", out var id) ? id.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray() : Array.Empty<string>(); return new { models };
    }
    private static async Task<object> RemoveWorktree(JsonElement payload)
    { await WorktreeManager.RemoveAsync(payload.Required("repository"), payload.Required("path"), CancellationToken.None); return new { removed = true }; }
    private object Cancel(string id)
    {
        if (active.TryGetValue(id, out var run)) run.Cancellation.Cancel();
        return new { accepted = true };
    }
    private object DeleteTask(string taskId)
    {
        var runs = store.History(1000).Where(r => r.TaskId == taskId).ToArray();
        if (runs.Length == 0) throw new InvalidOperationException("找不到要删除的任务。");
        if (runs.Any(r => active.ContainsKey(r.Id))) throw new InvalidOperationException("执行中的任务不能删除，请先停止执行。");
        store.DeleteTask(taskId);
        return new { deleted = true, taskId };
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
    private object Continue(JsonElement payload)
    {
        var parentId = payload.Required("runId");
        var parent = store.History().SingleOrDefault(r => r.Id == parentId)
            ?? throw new InvalidOperationException("找不到要继续的任务。");
        if (parent.Kind == "chat" ? parent.Status is not ("completed" or "interrupted") : parent.Status != "interrupted") throw new InvalidOperationException("只有已完成或被中断的对话可以继续执行；失败任务已停止。");
        var workspace = parent.Kind == "chat" ? (payload.TryGetProperty("workspace", out var value) ? value.GetString() ?? "" : "") : parent.Workspace;
        return Start(payload, parentId, workspace, parent.Provider, parent.Kind);
    }

    private static Task<object> OpenProject(JsonElement payload)
    {
        var workspace = Path.GetFullPath(payload.Required("workspace"));
        if (!Directory.Exists(workspace)) throw new InvalidOperationException("工作区目录不存在。");
        var isGit = Directory.Exists(Path.Combine(workspace, ".git")) || File.Exists(Path.Combine(workspace, ".git"));
        return Task.FromResult<object>(new { workspace, name = Path.GetFileName(workspace), isGit });
    }

    private object GetWorkspaceDiff(JsonElement payload)
    {
        var result = new List<object>();
        foreach (var artifact in store.Events(payload.Required("runId"))
                     .Where(e => e.Type == "artifact.created")
                     .Select(e => e.Payload))
        {
            var workspace = artifact.GetProperty("workspace").GetString()!;
            var path = artifact.GetProperty("path").GetString()!;
            var tools = new WorkspaceTools(workspace, (_, _, _) => Task.CompletedTask, (_, _) => { }, CancellationToken.None);
            var full = tools.Resolve(path);
            var exists = File.Exists(full);
            var current = exists ? File.ReadAllText(full) : "";
            var hash = WorkspaceTools.Hash(current);
            var artifactHash = artifact.GetProperty("hash").GetString()!;
            result.Add(new
            {
                artifactId = artifact.GetProperty("artifactId").GetString(),
                workspace,
                path,
                before = artifact.GetProperty("before").GetString(),
                after = artifact.GetProperty("after").GetString(),
                hash = artifactHash,
                existed = artifact.GetProperty("existed").GetBoolean(),
                message = artifact.TryGetProperty("message", out var message) ? message.GetString() : null,
                current,
                currentHash = hash,
                currentExists = exists,
                changedSinceArtifact = hash != artifactHash
            });
        }
        return result;
    }
    private object Start(JsonElement payload)
    {
        return Start(payload, null, payload.Required("workspace"), payload.Required("providerId"), "project");
    }
    private object StartChat(JsonElement payload) => Start(payload, null, payload.TryGetProperty("workspace", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? "" : "", payload.Required("providerId"), "chat");
    private object Start(JsonElement payload, string? parentId, string workspaceValue, string providerId, string kind)
    {
        if (!active.IsEmpty) throw new InvalidOperationException("首版本地执行器一次只运行一个任务，请等待或取消当前执行。");
        var workspace = string.IsNullOrWhiteSpace(workspaceValue) ? "" : Path.GetFullPath(workspaceValue);
        if ((kind == "project" || workspace.Length > 0) && !Directory.Exists(workspace)) throw new InvalidOperationException("工作区目录不存在。");
        var provider = providers.SingleOrDefault(p => p.Id == providerId) ?? throw new InvalidOperationException("提供商不存在。");
        if (!provider.Enabled) throw new InvalidOperationException("该模型服务已停用，请选择启用的模型服务。");
        var requestedModel = payload.TryGetProperty("model", out var modelValue) ? modelValue.GetString() : null;
        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            if (!provider.AvailableModels.Contains(requestedModel, StringComparer.Ordinal)) throw new InvalidOperationException("所选模型不属于该模型服务。");
            provider = provider with { Model = requestedModel };
        }
        provider.Validate(); provider.ResolveApiKey();
        var prompt = payload.Required("prompt");
        if (prompt.Length > 32000) throw new InvalidOperationException("任务说明过长，请控制在 32000 字符内。");
        var row = store.Create(kind, workspace, prompt, provider.Id, parentId);
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
            var tools = string.IsNullOrWhiteSpace(row.Workspace) ? null : new WorkspaceTools(row.Workspace, (name, arguments, ct) => Approvals.Require(row.Id, name, arguments, ct),
                (type, body) => Emit(row.Id, type, body), token);
            var agent = row.Kind == "chat" ? HarnessFactory.CreateChat(client, tools?.Functions() ?? [], tools is not null) : HarnessFactory.CreateCoding(client, tools!.Functions());
            var session = await agent.CreateSessionAsync(token);
            var pending = new StringBuilder();
            var messages = new List<ChatMessage>();
            if (row.Kind == "chat")
            {
                foreach (var previous in store.History(1000).Where(r => r.TaskId == row.TaskId && r.Id != row.Id).OrderBy(r => r.Created))
                {
                    messages.Add(new ChatMessage(ChatRole.User, previous.Prompt));
                    var answer = string.Concat(store.Events(previous.Id, limit: 5000).Where(e => e.Type == "message.delta").Select(e => e.Payload.GetProperty("text").GetString()));
                    if (answer.Length > 0) messages.Add(new ChatMessage(ChatRole.Assistant, answer));
                }
            }
            messages.Add(new ChatMessage(ChatRole.User, row.Prompt));
            await foreach (var update in agent.RunStreamingAsync(messages, session, cancellationToken: token))
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

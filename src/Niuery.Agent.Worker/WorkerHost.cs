using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Niuery.Agent.Runtime.Maf;
using Microsoft.Extensions.AI;

namespace Niuery.Agent.Worker;

public sealed class WorkerHost(Store store, IReadOnlyList<ProviderConfiguration> initialProviders, string configPath, Func<ProviderConfiguration, IChatClient>? clientFactory = null) : IAsyncDisposable
{
    /// <summary>
    /// Chat 模式默认使用的临时工作区。目录位于当前用户桌面，不依赖前端传入路径。
    /// </summary>
    public static string DefaultChatWorkspace
    {
        get
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop))
                desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
            return Path.Combine(desktop, "NiueryWorkSpace");
        }
    }

    private IReadOnlyList<ProviderConfiguration> providers = initialProviders;
    private readonly Func<ProviderConfiguration, IChatClient> clientFactory = clientFactory ?? HarnessFactory.CreateClient;
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cancellation, Task Work)> active = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> operationCancellations = new();
    private ApprovalGate? approvalGate;
    private ApprovalGate Approvals => approvalGate ??= new ApprovalGate((id, type, body) => Emit(id, type, body));
    public async Task<object> Handle(string method, JsonElement payload)
    {
        object result = method switch
        {
            "hello" => new { version = 1, eventSchemaVersion = 1, name = "Niuery Agent", capabilities = new[] { "streaming", "cancel", "history", "project.open", "chat.start", "mode.get", "mode.set", "workspace.diff", "workspace.gitDiff", "workspace.tree", "workspace.read", "workspace.apply", "workspace.command", "artifact.undo", "worktree.create", "worktree.remove", "worker.shutdown" } },
            "project.open" => await OpenProject(payload),
            "providers.list" => providers.Select(p => new { p.Id, p.Kind, p.BaseUrl, p.Model, Models = p.AvailableModels, p.ApiKey, p.SupportsTools, p.SupportsStreaming, p.TimeoutSeconds, p.Enabled, p.Transport, p.ReasoningOutput }).ToArray(),
            "providers.save" => await SaveProviders(payload),
            "providers.test" => await TestProvider(payload),
            "providers.sync" => await SyncProvider(payload),
            "history.list" => store.History(payload.TryGetProperty("limit", out var historyLimit) ? historyLimit.GetInt32() : 200, payload.TryGetProperty("offset", out var historyOffset) ? historyOffset.GetInt32() : 0),
            "events.list" => store.Events(payload.Required("runId"), payload.TryGetProperty("afterSequence", out var after) ? after.GetInt64() : 0, payload.TryGetProperty("limit", out var eventLimit) ? eventLimit.GetInt32() : 1000),
            "run.start" => Start(payload),
            "chat.start" => StartChat(payload),
            "task.delete" => DeleteTask(payload.Required("taskId")),
            "run.continue" => Continue(payload),
            "mode.get" => GetMode(payload.Required("taskId")),
            "mode.set" => SetMode(payload.Required("taskId"), payload.Required("mode")),
            "run.cancel" => Cancel(payload.Required("runId")),
            "approval.respond" => Approvals.Respond(payload.Required("runId"), payload.Required("approvalId"), payload.Required("digest"), payload.GetProperty("approved").GetBoolean()),
            "workspace.diff" => GetWorkspaceDiff(payload),
            "workspace.gitDiff" => await GetGitDiff(payload),
            "workspace.tree" => GetWorkspaceTree(payload),
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
            using var response = await SendWithRetry(client, url);
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
        var p = JsonSerializer.Deserialize<ProviderConfiguration>(payload.GetProperty("provider"), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("提供商参数无效。"); p.Validate(false); using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Min(p.TimeoutSeconds, 15)) }; var apiKey = p.Kind == "ollama" ? "ollama" : p.ResolveApiKey(false); if (p.Kind == "openai-compatible") client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey); var url = new Uri(new Uri(p.BaseUrl.EndsWith('/') ? p.BaseUrl : p.BaseUrl + "/"), "models"); using var response = await SendWithRetry(client, url); response.EnsureSuccessStatusCode(); var json = await response.Content.ReadAsStringAsync(); using var doc = JsonDocument.Parse(json); var models = doc.RootElement.TryGetProperty("data", out var data) ? data.EnumerateArray().Select(x => x.TryGetProperty("id", out var id) ? id.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray() : Array.Empty<string>(); return new { models };
    }
    private static async Task<HttpResponseMessage> SendWithRetry(HttpClient client, Uri url)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await client.GetAsync(url);
                if (attempt < 2 && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500))
                {
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)));
                    continue;
                }
                return response;
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)));
            }
        }
    }
    private static async Task<object> RemoveWorktree(JsonElement payload)
    { await WorktreeManager.RemoveAsync(payload.Required("repository"), payload.Required("path"), CancellationToken.None); return new { removed = true }; }
    private object Cancel(string id)
    {
        if (active.TryGetValue(id, out var run)) run.Cancellation.Cancel();
        if (operationCancellations.TryGetValue(id, out var operation)) operation.Cancel();
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
        var runId = copy.Required("runId");
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        operationCancellations[runId] = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                var root = Path.GetFullPath(copy.Required("workspace"));
                var executable = copy.Required("executable");
                var arguments = copy.GetProperty("arguments").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                ValidateCommandPolicy(executable, arguments);
                await Approvals.Require(runId, "运行命令", new { executable, arguments, root }, cancellation.Token);
                var result = await WorkspaceTools.Execute(executable, arguments, root, cancellation.Token);
                Emit(runId, "command.completed", new { result.ExitCode, result.Output, result.Truncated, message = $"命令完成，退出码 {result.ExitCode}" });
            }
            catch (OperationCanceledException) { Emit(runId, "command.cancelled", new { message = "命令已取消。" }); }
            catch (Exception) { Emit(runId, "command.failed", new { message = "命令执行失败或被拒绝。" }); }
            finally { operationCancellations.TryRemove(runId, out _); cancellation.Dispose(); }
        });
        return new { accepted = true };
    }
    private static void ValidateCommandPolicy(string executable, IReadOnlyList<string> arguments)
    {
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        var allowed = new[] { "dotnet", "git", "npm", "node", "pwsh", "powershell" };
        if (!allowed.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("该命令不在允许列表中。");
        var joined = string.Join(" ", arguments);
        var blocked = new[] { "rm -rf", "rmdir /s", "del /s", "format ", "shutdown", "reg delete", "credential", ".ssh", ".env" };
        if (blocked.Any(token => joined.Contains(token, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("命令参数触发了安全策略。");
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
        if (parent.Status is not ("completed" or "interrupted")) throw new InvalidOperationException("只有已完成或被中断的任务可以继续执行；失败任务已停止。");
        var workspace = parent.Kind == "chat" ? (payload.TryGetProperty("workspace", out var value) ? value.GetString() ?? "" : "") : parent.Workspace;
        return Start(payload, parentId, workspace, parent.Provider, parent.Kind, parent.TaskId);
    }

    private object GetMode(string taskId)
    {
        if (!store.History(1000).Any(run => run.TaskId == taskId))
            throw new InvalidOperationException("找不到指定任务。");
        return new { taskId, mode = store.LoadTaskState(taskId)?.Mode ?? "execute" };
    }

    private object SetMode(string taskId, string mode)
    {
        ValidateMode(mode);
        var taskRuns = store.History(1000).Where(run => run.TaskId == taskId).ToArray();
        if (taskRuns.Length == 0) throw new InvalidOperationException("找不到指定任务。");
        if (taskRuns.Any(run => active.ContainsKey(run.Id)))
            throw new InvalidOperationException("任务执行中不能切换模式，请等待任务完成。");
        var current = store.LoadTaskState(taskId);
        var previous = current?.Mode ?? "execute";
        if (previous == mode) return new { taskId, mode };
        store.SaveTaskState(taskId, mode, current?.SessionJson);
        var run = taskRuns.OrderByDescending(item => item.Created).First();
        Emit(run.Id, "mode.changed", new { from = previous, to = mode, source = "user", message = $"已从 {previous} 模式切换到 {mode} 模式。" });
        return new { taskId, mode };
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
    private static object GetWorkspaceTree(JsonElement payload)
    {
        var root = Path.GetFullPath(payload.Required("workspace"));
        if (!Directory.Exists(root)) throw new InvalidOperationException("工作区目录不存在。");
        var tools = new WorkspaceTools(root, (_, _, _) => Task.CompletedTask, (_, _) => { }, CancellationToken.None);
        var files = tools.WorkspaceFiles().Take(5000).ToArray();
        return new { root, files };
    }
    private static async Task<object> GetGitDiff(JsonElement payload)
    {
        var root = Path.GetFullPath(payload.Required("workspace"));
        if (!Directory.Exists(root)) throw new InvalidOperationException("工作区目录不存在。");
        var statusResult = await WorkspaceTools.Execute("git", ["--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=all"], root, CancellationToken.None);
        if (statusResult.ExitCode != 0) throw new InvalidOperationException("当前目录不是可用的 Git 工作区。");
        var prefixResult = await WorkspaceTools.Execute("git", ["rev-parse", "--show-prefix"], root, CancellationToken.None);
        var repoPrefix = prefixResult.ExitCode == 0 ? prefixResult.Output.Trim().Replace('\\', '/') : "";
        var branchResult = await WorkspaceTools.Execute("git", ["branch", "--show-current"], root, CancellationToken.None);
        var files = new List<object>();
        foreach (var raw in statusResult.Output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 4) continue;
            var code = raw[..2];
            var repoPath = raw[3..].Trim();
            if (repoPath.Contains(" -> ", StringComparison.Ordinal)) repoPath = repoPath[(repoPath.LastIndexOf(" -> ", StringComparison.Ordinal) + 4)..];
            var path = repoPrefix.Length > 0 && repoPath.StartsWith(repoPrefix, StringComparison.Ordinal) ? repoPath[repoPrefix.Length..] : repoPath;
            // 即使文件已经被 Git 跟踪，.gitignore 规则仍可能明确将其标记为忽略。
            // 审查视图应遵循当前仓库的忽略规则，避免把 bin/obj 等生成物展示出来。
            if (await IsGitIgnored(root, repoPath)) continue;
            var full = Path.Combine(root, path);
            var after = File.Exists(full) ? await File.ReadAllTextAsync(full) : "";
            var beforeResult = await WorkspaceTools.Execute("git", ["show", $"HEAD:{repoPath}"], root, CancellationToken.None);
            var before = beforeResult.ExitCode == 0 ? beforeResult.Output : "";
            var diffResult = await WorkspaceTools.Execute("git", ["--no-pager", "diff", "HEAD", "--numstat", "--", path], root, CancellationToken.None);
            var nums = diffResult.Output.Replace("\r", "").Split('\n').Select(line => line.Split('\t')).FirstOrDefault(parts => parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _));
            var additions = nums is not null && int.TryParse(nums[0], out var a) ? a : (before.Length == 0 ? CountLines(after) : 0);
            var deletions = nums is not null && int.TryParse(nums[1], out var d) ? d : 0;
            files.Add(new { path, status = code.Trim(), before, after, additions, deletions });
        }
        return new { branch = branchResult.Output.Trim(), files, totalAdditions = files.Sum(x => (int)x.GetType().GetProperty("additions")!.GetValue(x)!), totalDeletions = files.Sum(x => (int)x.GetType().GetProperty("deletions")!.GetValue(x)!) };
    }
    private static async Task<bool> IsGitIgnored(string root, string path)
    {
        var result = await WorkspaceTools.Execute("git", ["check-ignore", "--no-index", "--quiet", "--", path], root, CancellationToken.None);
        return result.ExitCode == 0;
    }
    private static int CountLines(string text) => string.IsNullOrEmpty(text) ? 0 : text.Replace("\r\n", "\n").Split('\n').Length - (text.EndsWith('\n') ? 1 : 0);
    private object Start(JsonElement payload)
    {
        return Start(payload, null, payload.Required("workspace"), payload.Required("providerId"), "project");
    }
    private object StartChat(JsonElement payload) => Start(payload, null, payload.TryGetProperty("workspace", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? "" : "", payload.Required("providerId"), "chat");
    private object Start(JsonElement payload, string? parentId, string workspaceValue, string providerId, string kind, string? taskId = null)
    {
        if (!active.IsEmpty) throw new InvalidOperationException("首版本地执行器一次只运行一个任务，请等待或取消当前执行。");
        var workspace = string.IsNullOrWhiteSpace(workspaceValue)
            ? (kind == "chat" ? DefaultChatWorkspace : "")
            : Path.GetFullPath(workspaceValue);
        if (kind == "chat" && string.IsNullOrWhiteSpace(workspaceValue))
            Directory.CreateDirectory(workspace);
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
        if (kind == "project") EnsureGitBaseline(workspace);
        var mode = ResolveMode(payload, taskId);
        var row = store.Create(kind, workspace, prompt, provider.Id, parentId);
        var previousState = store.LoadTaskState(row.TaskId);
        store.SaveTaskState(row.TaskId, mode, previousState?.SessionJson);
        if (previousState is not null && previousState.Mode != mode)
            Emit(row.Id, "mode.changed", new { from = previousState.Mode, to = mode, source = "user", message = $"已从 {previousState.Mode} 模式切换到 {mode} 模式。" });
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(provider.TimeoutSeconds));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () => { await ready.Task; await Run(row, provider, cancellation.Token); });
        active[row.Id] = (cancellation, task); ready.SetResult();
        return row;
    }

    private static void EnsureGitBaseline(string workspace)
    {
        var gitPath = Path.Combine(workspace, ".git");
        if (Directory.Exists(gitPath) || File.Exists(gitPath)) return;
        if (Directory.EnumerateFileSystemEntries(workspace).Any()) return;

        RunGit(workspace, ["init"]);
        // 使用仓库级身份，避免要求用户先配置全局 Git 身份。
        RunGit(workspace, ["-c", "user.name=Niuery Agent", "-c", "user.email=agent@localhost", "add", "--all"]);
        RunGit(workspace, ["-c", "user.name=Niuery Agent", "-c", "user.email=agent@localhost", "commit", "--allow-empty", "-m", "初始化项目基线"]);
    }

    private static void RunGit(string workspace, string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = workspace, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("无法启动 Git，请安装 Git 后重试。");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Git 初始化失败：{process.StandardError.ReadToEnd().Trim()}");
    }

    private string ResolveMode(JsonElement payload, string? taskId)
    {
        if (payload.TryGetProperty("mode", out var modeValue) && modeValue.ValueKind == JsonValueKind.String)
            return ValidateMode(modeValue.GetString());
        return taskId is null ? "execute" : ValidateMode(store.LoadTaskState(taskId)?.Mode ?? "execute");
    }

    private static string ValidateMode(string? mode)
    {
        if (mode is not ("plan" or "execute"))
            throw new InvalidOperationException("任务模式必须是 plan 或 execute。");
        return mode;
    }
    private void Emit(string runId, string type, object payload, string? status = null)
    {
        var item = store.Append(runId, type, payload, status);
        Wire.Send(new { version = 1, eventSchemaVersion = 1, eventData = item });
    }
    private async Task Run(RunRow row, ProviderConfiguration provider, CancellationToken token)
    {
        var status = "completed";
        var message = "执行完成。";
        AIAgent? agent = null;
        AgentSession? session = null;
        var restored = false;
        try
        {
            Emit(row.Id, "run.started", new { message = "执行已开始。", provider.Model, processId = Environment.ProcessId, startedAt = DateTimeOffset.UtcNow });
            using var client = clientFactory(provider);
            var tools = string.IsNullOrWhiteSpace(row.Workspace) ? null : new WorkspaceTools(row.Workspace, (name, arguments, ct) => Approvals.Require(row.Id, name, arguments, ct),
                (type, body) => Emit(row.Id, type, body), token);
            var taskState = store.LoadTaskState(row.TaskId);
            var mode = ValidateMode(taskState?.Mode ?? "execute");
            agent = row.Kind == "chat" ? HarnessFactory.CreateChat(client, tools?.Functions() ?? [], tools is not null, mode, provider.ReasoningOutput) : HarnessFactory.CreateCoding(client, tools!.Functions(), mode, provider.ReasoningOutput);
            if (!string.IsNullOrWhiteSpace(taskState?.SessionJson))
            {
                try
                {
                    var snapshot = JsonSerializer.Deserialize<JsonElement>(taskState.SessionJson);
                    session = await agent.DeserializeSessionAsync(snapshot, cancellationToken: token);
                    restored = true;
                    Emit(row.Id, "session.restored", new { message = "已恢复任务会话。" });
                }
                catch (Exception)
                {
                    Emit(row.Id, "session.restore.failed", new { message = "任务会话恢复失败，已创建新会话。" });
                }
            }
            session ??= await agent.CreateSessionAsync(token);
            var modeProvider = agent.GetService<AgentModeProvider>();
            if (modeProvider is not null) await modeProvider.SetModeAsync(session, mode, token);
            var pending = new StringBuilder();
            var reasoningPending = new StringBuilder();
            var hasReasoning = false;
            var messages = new List<ChatMessage>();
            if (!restored && row.Kind == "chat")
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
                token.ThrowIfCancellationRequested();
                pending.Append(update.Text);
                foreach (var reasoning in update.Contents.OfType<TextReasoningContent>())
                {
                    if (!string.IsNullOrEmpty(reasoning.Text))
                    {
                        hasReasoning = true;
                        reasoningPending.Append(reasoning.Text);
                    }
                }
                if (reasoningPending.Length >= 80)
                {
                    Emit(row.Id, "reasoning.delta", new { text = reasoningPending.ToString(), format = provider.ReasoningOutput });
                    reasoningPending.Clear();
                }
                if (pending.Length >= 80) { Emit(row.Id, "message.delta", new { text = pending.ToString() }); pending.Clear(); }
            }
            if (reasoningPending.Length > 0) Emit(row.Id, "reasoning.delta", new { text = reasoningPending.ToString(), format = provider.ReasoningOutput });
            if (hasReasoning) Emit(row.Id, "reasoning.completed", new { format = provider.ReasoningOutput });
            if (pending.Length > 0) Emit(row.Id, "message.delta", new { text = pending.ToString() });
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { status = "cancelled"; message = "执行已取消或超时。"; }
        catch (Exception ex) { status = "failed"; message = $"执行失败：{ex.Message}"; }
        finally
        {
            if (agent is not null && session is not null)
            {
                try
                {
                    var modeProvider = agent.GetService<AgentModeProvider>();
                    var mode = modeProvider is null ? (store.LoadTaskState(row.TaskId)?.Mode ?? "execute") : await modeProvider.GetModeAsync(session, CancellationToken.None);
                    var snapshot = await agent.SerializeSessionAsync(session, cancellationToken: CancellationToken.None);
                    store.SaveTaskState(row.TaskId, ValidateMode(mode), snapshot.GetRawText());
                }
                catch (Exception ex)
                {
                    status = "failed";
                    message = $"任务状态保存失败：{ex.Message}";
                    Emit(row.Id, "session.save.failed", new { message });
                }
            }
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

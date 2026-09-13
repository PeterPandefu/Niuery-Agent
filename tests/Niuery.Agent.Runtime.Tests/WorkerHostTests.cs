using System.Text.Json;
using Niuery.Agent.Runtime.Maf;
using Niuery.Agent.Worker;
using Xunit;

namespace Niuery.Agent.Runtime.Tests;

public sealed class WorkerHostTests
{
    private static ProviderConfiguration Provider => new(
        "test", "ollama", "http://127.0.0.1:11434/v1/", "model", null);

    [Fact(DisplayName = "握手能力声明包含工作区命令执行")]
    public async Task HelloAdvertisesWorkspaceCommandCapability()
    {
        var directory = Directory.CreateTempSubdirectory("niuery-hello-").FullName;
        var config = Path.Combine(directory, "providers.json");
        await File.WriteAllTextAsync(config, "{}");
        await using var host = new WorkerHost(new Store(Path.Combine(directory, "tasks.db")), [Provider], config);

        var response = JsonSerializer.SerializeToElement(await host.Handle("hello", default));
        var capabilities = response.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString());

        Assert.Contains("workspace.command", capabilities);
    }

    [Fact(DisplayName = "项目打开接受 Git 和非 Git 目录并标记仓库状态")]
    public async Task ProjectOpenAcceptsAnyWorkspace()
    {
        var directory = Directory.CreateTempSubdirectory("niuery-project-").FullName;
        var config = Path.Combine(directory, "providers.json");
        await File.WriteAllTextAsync(config, "{}");
        await using var host = new WorkerHost(new Store(Path.Combine(directory, "tasks.db")), [Provider], config);
        var payload = JsonSerializer.SerializeToElement(new { workspace = directory });

        var openedWithoutGit = await host.Handle("project.open", payload);
        Assert.Contains(directory, openedWithoutGit.ToString());
        Assert.Contains("False", openedWithoutGit.ToString());

        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        var opened = await host.Handle("project.open", payload);
        Assert.Contains(directory, opened.ToString());
        Assert.Contains("True", opened.ToString());
    }

    [Fact(DisplayName = "项目任务完成后可以继续执行")]
    public async Task ContinueAcceptsCompletedProjectRun()
    {
        var directory = Directory.CreateTempSubdirectory("niuery-continue-").FullName;
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        var config = Path.Combine(directory, "providers.json");
        await File.WriteAllTextAsync(config, "{}");
        using var store = new Store(Path.Combine(directory, "tasks.db"));
        var run = store.Create(directory, "已完成", Provider.Id);
        store.Append(run.Id, "run.completed", new { message = "完成" }, "completed");
        await using var host = new WorkerHost(store, [Provider], config);

        var payload = JsonSerializer.SerializeToElement(new { runId = run.Id, prompt = "继续" });
        var next = await host.Handle("run.continue", payload);
        Assert.Contains(run.TaskId, next.ToString());
    }

    [Fact(DisplayName = "任务模式可以读取并由用户切换")]
    public async Task TaskModeCanBeReadAndChanged()
    {
        var directory = Directory.CreateTempSubdirectory("niuery-mode-").FullName;
        var config = Path.Combine(directory, "providers.json");
        await File.WriteAllTextAsync(config, "{}");
        using var store = new Store(Path.Combine(directory, "tasks.db"));
        var run = store.Create(directory, "模式测试", Provider.Id);
        store.Append(run.Id, "run.completed", new { message = "完成" }, "completed");
        store.SaveTaskState(run.TaskId, "plan", null);
        await using var host = new WorkerHost(store, [Provider], config);

        var initial = await host.Handle("mode.get", JsonSerializer.SerializeToElement(new { taskId = run.TaskId }));
        Assert.Contains("plan", initial.ToString());

        var changed = await host.Handle("mode.set", JsonSerializer.SerializeToElement(new { taskId = run.TaskId, mode = "execute" }));
        Assert.Contains("execute", changed.ToString());
        Assert.Equal("execute", store.LoadTaskState(run.TaskId)?.Mode);
        Assert.Contains(store.Events(run.Id), item => item.Type == "mode.changed" && item.Payload.GetProperty("source").GetString() == "user");
    }

    [Fact(DisplayName = "Worker 持久化 reasoning 增量并与最终回答分离")]
    public async Task WorkerPersistsReasoningEventsSeparately()
    {
        var directory = Directory.CreateTempSubdirectory("niuery-reasoning-").FullName;
        var config = Path.Combine(directory, "providers.json");
        await File.WriteAllTextAsync(config, "{}");
        using var store = new Store(Path.Combine(directory, "tasks.db"));
        using var client = new ScriptedChatClient { IncludeReasoning = true, TextOnly = true };
        await using var host = new WorkerHost(store, [Provider with { ReasoningOutput = "summary" }], config, _ => client);

        var run = (RunRow)await host.Handle("chat.start", JsonSerializer.SerializeToElement(new { providerId = Provider.Id, prompt = "总结请求。", workspace = directory }));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (store.History().Single(item => item.Id == run.Id).Status == "running" && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var events = store.Events(run.Id);
        Assert.Contains(events, item => item.Type == "reasoning.delta" && item.Payload.GetProperty("text").GetString()!.Contains("分析请求"));
        Assert.Contains(events, item => item.Type == "reasoning.completed");
        Assert.Equal("脚本模型最终回答。", string.Concat(events.Where(item => item.Type == "message.delta").Select(item => item.Payload.GetProperty("text").GetString())));
    }
}

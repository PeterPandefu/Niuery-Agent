using System.Text.Json;
using Niuery.Agent.Runtime.Maf;
using Niuery.Agent.Worker;
using Xunit;

namespace Niuery.Agent.Runtime.Tests;

public sealed class WorkerHostTests
{
    private static ProviderConfiguration Provider => new(
        "test", "ollama", "http://127.0.0.1:11434/v1/", "model", null);

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

    [Fact(DisplayName = "继续任务只允许被中断的执行")]
    public async Task ContinueRejectsCompletedRun()
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
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Handle("run.continue", payload));
        Assert.Contains("中断", error.Message);
    }
}

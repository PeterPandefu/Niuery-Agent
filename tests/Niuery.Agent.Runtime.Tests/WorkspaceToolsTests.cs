using Niuery.Agent.Worker;
using System.Text.Json;
using Xunit;

namespace Niuery.Agent.Runtime.Tests;

public sealed class WorkspaceToolsTests
{
    [Fact(DisplayName = "拒绝越界路径、秘密目录和符号链接")]
    public void PathPolicyRejectsEscape()
    {
        var root = Directory.CreateTempSubdirectory("niuery-tools-").FullName;
        var tools = new WorkspaceTools(root, (_, _, _) => Task.CompletedTask, (_, _) => { }, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => tools.Resolve("..\\outside.txt"));
        Assert.Throws<InvalidOperationException>(() => tools.Resolve(".env"));
        Assert.Throws<InvalidOperationException>(() => tools.Resolve(".ssh\\id_rsa"));
    }

    [Fact(DisplayName = "补丁版本不一致时不会覆盖用户修改")]
    public async Task PatchRequiresExpectedHash()
    {
        var root = Directory.CreateTempSubdirectory("niuery-tools-").FullName;
        var file = Path.Combine(root, "sample.txt"); await File.WriteAllTextAsync(file, "before");
        var tools = new WorkspaceTools(root, (_, _, _) => Task.CompletedTask, (_, _) => { }, CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ApplyPatch("sample.txt", WorkspaceTools.Hash("other"), "before", "after"));
        Assert.Contains("版本", error.Message);
        Assert.Equal("before", await File.ReadAllTextAsync(file));
    }

    [Fact(DisplayName = "命令超时后终止进程树")]
    public async Task CommandTimeoutCancels()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkspaceTools.Execute("powershell", ["-NoProfile", "-Command", "Start-Sleep -Seconds 10"], Environment.CurrentDirectory, timeout.Token));
    }

    [Fact(DisplayName = "命令创建的非 Git 文件会生成差异制品")]
    public async Task CommandChangesCreateArtifacts()
    {
        var root = Directory.CreateTempSubdirectory("niuery-tools-").FullName;
        var events = new List<(string Type, object Payload)>();
        var tools = new WorkspaceTools(root, (_, _, _) => Task.CompletedTask, (type, payload) => events.Add((type, payload)), CancellationToken.None);

        await tools.RunCommand("powershell", ["-NoProfile", "-Command", "Set-Content -LiteralPath created.txt -Value 'hello' -NoNewline"]);

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(root, "created.txt")));
        var artifact = events.Single(e => e.Type == "artifact.created").Payload;
        var json = JsonSerializer.SerializeToElement(artifact);
        Assert.Equal("created.txt", json.GetProperty("path").GetString());
        Assert.False(json.GetProperty("existed").GetBoolean());
        Assert.Equal("hello", json.GetProperty("after").GetString());
    }

}

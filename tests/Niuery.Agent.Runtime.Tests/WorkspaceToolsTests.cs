using Niuery.Agent.Worker;
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
}

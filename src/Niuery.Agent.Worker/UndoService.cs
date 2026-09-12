using System.Text.Json;

namespace Niuery.Agent.Worker;

public sealed class UndoService
{
    public static async Task UndoAsync(JsonElement artifact, CancellationToken token)
    {
        var path = artifact.GetProperty("path").GetString()!;
        var tools = new WorkspaceTools(artifact.GetProperty("workspace").GetString()!, (_, _, _) => Task.CompletedTask, (_, _) => { }, token);
        var full = tools.Resolve(path);
        var expected = artifact.GetProperty("hash").GetString()!;
        var current = File.Exists(full) ? await File.ReadAllTextAsync(full, token) : "";
        if (WorkspaceTools.Hash(current) != expected) throw new InvalidOperationException("文件在生成制品后已变化，拒绝覆盖当前修改。");
        var existed = artifact.GetProperty("existed").GetBoolean();
        var before = artifact.GetProperty("before").GetString()!;
        if (existed) await File.WriteAllTextAsync(full, before, new System.Text.UTF8Encoding(false), token);
        else if (File.Exists(full)) File.Delete(full);
    }
}

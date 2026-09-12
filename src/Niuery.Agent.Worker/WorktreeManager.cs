using System.Diagnostics;

namespace Niuery.Agent.Worker;

public sealed class WorktreeManager
{
    public static async Task<string> CreateAsync(string repository, string name, CancellationToken token)
    {
        var root = Path.GetFullPath(repository);
        if (!Directory.Exists(Path.Combine(root, ".git"))) throw new InvalidOperationException("目标目录不是 Git 仓库。");
        if (string.IsNullOrWhiteSpace(name) || name.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_'))) throw new InvalidOperationException("Worktree 名称只能包含字母、数字、连字符和下划线。");
        var path = Path.Combine(Path.GetDirectoryName(root)!, $"{Path.GetFileName(root)}.agent-{name}");
        if (Directory.Exists(path)) throw new InvalidOperationException("Worktree 目录已存在。");
        var result = await Run("git", ["worktree", "add", "--detach", path, "HEAD"], root, token);
        if (result.ExitCode != 0) throw new InvalidOperationException("Git worktree 创建失败。");
        return path;
    }
    public static async Task RemoveAsync(string repository, string path, CancellationToken token)
    {
        var root = Path.GetFullPath(repository); var target = Path.GetFullPath(path);
        if (!target.StartsWith(Path.GetDirectoryName(root)!, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Worktree 路径不在仓库邻近目录。");
        var result = await Run("git", ["worktree", "remove", "--force", target], root, token);
        if (result.ExitCode != 0) throw new InvalidOperationException("Git worktree 删除失败。");
    }
    private static async Task<CommandResult> Run(string file, string[] args, string cwd, CancellationToken token) => await WorkspaceTools.Execute(file, args, cwd, token);
}

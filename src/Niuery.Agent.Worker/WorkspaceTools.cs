using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Niuery.Agent.Worker;

public sealed class WorkspaceTools(string root, Func<string, object, CancellationToken, Task> approve,
    Action<string, object> emit, CancellationToken runToken)
{
    private readonly string workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    private readonly SemaphoreSlim mutations = new(1, 1);
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".vs", ".env", ".ssh" };
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidOperationException("路径必须是工作区内的相对路径。");
        var full = Path.GetFullPath(Path.Combine(workspace, relative));
        if (!full.StartsWith(workspace + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("拒绝访问工作区以外的路径。");
        var cursor = workspace;
        if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("工作区不能是链接目录。");
        foreach (var part in Path.GetRelativePath(workspace, full).Split(Path.DirectorySeparatorChar))
        {
            if (Excluded.Contains(part) || part.StartsWith(".env", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("该路径被项目访问策略排除。");
            cursor = Path.Combine(cursor, part);
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("拒绝通过符号链接或目录联接访问文件。");
        }
        return full;
    }
    public IEnumerable<AITool> Functions() =>
    [
        AIFunctionFactory.Create(ListFiles, "ListFiles"), AIFunctionFactory.Create(ReadFile, "ReadFile"),
        AIFunctionFactory.Create(Search, "Search"), AIFunctionFactory.Create(ApplyPatch, "ApplyPatch"),
        AIFunctionFactory.Create(RunCommand, "RunCommand"), AIFunctionFactory.Create(GitStatus, "GitStatus")
    ];
    private IEnumerable<string> Enumerate(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            runToken.ThrowIfCancellationRequested();
            if (Excluded.Contains(Path.GetFileName(entry)) || Path.GetFileName(entry).StartsWith(".env", StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
            if (Directory.Exists(entry)) { foreach (var file in Enumerate(entry)) yield return file; }
            else yield return Path.GetRelativePath(workspace, entry);
        }
    }
    public IEnumerable<string> WorkspaceFiles() => Enumerate(workspace);
    private async Task<T> Tool<T>(string name, object arguments, Func<Task<T>> action)
    {
        runToken.ThrowIfCancellationRequested(); var toolCallId = Guid.NewGuid().ToString("N");
        emit("tool.started", new { toolCallId, name, arguments, message = $"正在执行工具：{name}" });
        try { var result = await action(); emit("tool.completed", new { toolCallId, name, result, message = $"工具完成：{name}" }); return result; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex is InvalidOperationException ? ex.Message : "工具执行失败，请检查路径、权限或命令。";
            emit("tool.completed", new { toolCallId, name, error = message, message });
            throw new InvalidOperationException(message);
        }
    }
    [Description("列出工作区文件，排除依赖、构建目录和秘密文件；最多返回 1000 个文件。")]
    public Task<string[]> ListFiles() => Tool("ListFiles", new { }, () => Task.FromResult(Enumerate(workspace).Take(1000).ToArray()));
    [Description("分段读取文本文件，返回完整文件哈希供补丁校验。startLine 从 1 开始，最多读取 400 行。")]
    public Task<object> ReadFile(string path, int startLine = 1, int lineCount = 200) => Tool<object>("ReadFile", new { path, startLine, lineCount }, async () =>
    {
        if (startLine < 1 || lineCount is < 1 or > 400) throw new InvalidOperationException("读取行范围不合法。");
        var full = Resolve(path); if (new FileInfo(full).Length > 256000) throw new InvalidOperationException("文件超过 256 KB 读取上限。");
        var text = await File.ReadAllTextAsync(full, runToken); if (text.Contains('\0')) throw new InvalidOperationException("不能读取二进制文件。");
        return new { path, hash = Hash(text), text = string.Join('\n', text.Split('\n').Skip(startLine - 1).Take(lineCount)) };
    });
    [Description("搜索工作区中的文本字面量，最多返回 100 条命中。")]
    public Task<object> Search(string query) => Tool<object>("Search", new { query }, async () =>
    {
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("搜索内容不能为空。");
        var matches = new List<object>();
        foreach (var file in Enumerate(workspace).Take(1000))
        {
            var full = Resolve(file); if (new FileInfo(full).Length > 256000) continue;
            var lines = await File.ReadAllLinesAsync(full, runToken);
            for (var i = 0; i < lines.Length; i++) if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
            { matches.Add(new { path = file, line = i + 1, text = lines[i][..Math.Min(lines[i].Length, 300)] }); if (matches.Count == 100) return matches; }
        }
        return matches;
    });
    [Description("以精确文本替换应用单处补丁，expectedHash 必须是 ReadFile 返回的哈希。新文件使用空 expectedHash 和空 oldText。修改需要用户审批。")]
    public Task<object> ApplyPatch(string path, string expectedHash, string oldText, string newText) => Tool<object>("ApplyPatch", new { path, expectedHash }, async () =>
    {
        await mutations.WaitAsync(runToken);
        try
        {
            var full = Resolve(path); var existed = File.Exists(full);
            var before = existed ? await File.ReadAllTextAsync(full, runToken) : "";
            if ((existed && Hash(before) != expectedHash) || (!existed && (expectedHash != "" || oldText != ""))) throw new InvalidOperationException("文件版本已变化，请重新读取后生成补丁。");
            if (newText.Length > 128000 || before.Length > 256000) throw new InvalidOperationException("补丁超过大小限制。");
            string after;
            if (!existed) after = newText;
            else
            {
                var index = before.IndexOf(oldText, StringComparison.Ordinal);
                if (oldText.Length == 0 || index < 0 || before.IndexOf(oldText, index + oldText.Length, StringComparison.Ordinal) >= 0) throw new InvalidOperationException("旧文本必须在文件中唯一匹配。");
                after = before[..index] + newText + before[(index + oldText.Length)..];
            }
            await approve("修改文件", new { path, before, after, expectedHash, existed }, runToken);
            runToken.ThrowIfCancellationRequested(); Resolve(path);
            if (File.Exists(full) != existed || (existed && Hash(await File.ReadAllTextAsync(full, runToken)) != expectedHash)) throw new InvalidOperationException("审批期间文件被修改，补丁已取消。");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, after, new UTF8Encoding(false), runToken);
            var artifactId = Guid.NewGuid().ToString("N");
            emit("artifact.created", new { artifactId, workspace, path, before, after, existed, hash = Hash(after), message = $"已修改文件：{path}" });
            return new { path, hash = Hash(after), artifactId };
        }
        finally { mutations.Release(); }
    });
    [Description("审批后运行程序及参数数组。禁止把整个命令行放入 executable；需要 shell 时显式使用 powershell 或 cmd。命令不具备 OS 沙箱隔离，输出最多 16000 字符，60 秒超时。")]
    public Task<object> RunCommand(string executable, string[] arguments) => Tool<object>("RunCommand", new { executable, arguments }, async () =>
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Length > 1024 || arguments.Sum(a => a.Length) > 16000) throw new InvalidOperationException("命令参数不合法。");
        await approve("运行命令", new { executable, arguments, workspace, warning = "此命令使用你的本机权限，可访问工作区外部资源。" }, runToken);
        // 命令可能直接创建或修改文件（例如 dotnet new console），不会经过 ApplyPatch。
        // 在命令前后记录文本文件快照，统一生成差异制品，确保非 Git 工作区也能显示和撤销成果。
        var before = CaptureTextFiles();
        try { return await Execute(executable, arguments, workspace, runToken); }
        finally { EmitFileChanges(before); }
    });

    private Dictionary<string, string> CaptureTextFiles()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in Enumerate(workspace).Take(1000))
        {
            try
            {
                var full = Resolve(relative);
                if (new FileInfo(full).Length > 256000) continue;
                var text = File.ReadAllText(full);
                if (!text.Contains('\0')) snapshot[relative] = text;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return snapshot;
    }

    private void EmitFileChanges(IReadOnlyDictionary<string, string> before)
    {
        var after = CaptureTextFiles();
        foreach (var path in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var existed = before.TryGetValue(path, out var beforeText);
            var exists = after.TryGetValue(path, out var afterText);
            if (existed == exists && string.Equals(beforeText, afterText, StringComparison.Ordinal)) continue;
            var afterValue = afterText ?? "";
            emit("artifact.created", new
            {
                artifactId = Guid.NewGuid().ToString("N"), workspace, path,
                before = beforeText ?? "", after = afterValue, existed,
                hash = Hash(afterValue), message = $"命令修改文件：{path}"
            });
        }
    }
    [Description("只读查询 Git 状态与差异；不提交或推送。")]
    public Task<object> GitStatus() => Tool<object>("GitStatus", new { }, async () => new
    {
        status = await Execute("git", ["--no-optional-locks", "status", "--short"], workspace, runToken),
        diff = await Execute("git", ["--no-pager", "diff", "--no-ext-diff", "--no-textconv"], workspace, runToken)
    });
    public static async Task<CommandResult> Execute(string executable, string[] arguments, string cwd, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var info = new ProcessStartInfo(executable) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("命令无法启动。");
        var buffer = new StringBuilder(); var gate = new object(); var truncated = false;
        async Task Drain(StreamReader stream)
        {
            var chunk = new char[2048]; int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0) lock (gate)
            { var room = Math.Max(0, 16000 - buffer.Length); buffer.Append(chunk, 0, Math.Min(room, count)); truncated |= count > room; }
        }
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            throw;
        }
        return new(process.ExitCode, buffer.ToString(), truncated);
    }
}
public sealed record CommandResult(int ExitCode, string Output, bool Truncated);

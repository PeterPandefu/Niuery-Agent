using System.Collections.Concurrent;
using System.Text.Json;

namespace Niuery.Agent.Worker;

public sealed class ApprovalGate(Action<string, string, object> emit)
{
    private sealed record Pending(string RunId, string Digest, TaskCompletionSource<bool> Completion);
    private readonly ConcurrentDictionary<string, Pending> pending = new();
    public async Task Require(string runId, string name, object arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N"); var digest = WorkspaceTools.Hash(JsonSerializer.Serialize(arguments, Wire.Json));
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = new(runId, digest, completion);
        try
        {
            emit(runId, "approval.requested", new { approvalId = id, digest, name, arguments, message = $"需要批准：{name}" });
            var approved = await completion.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (!approved) throw new InvalidOperationException("用户拒绝了本次操作。");
        }
        finally { pending.TryRemove(id, out _); }
    }
    public object Respond(string runId, string approvalId, string digest, bool approved)
    {
        if (!pending.TryGetValue(approvalId, out var item) || item.RunId != runId || item.Digest != digest) throw new InvalidOperationException("审批已失效或与当前操作不匹配。");
        if (!pending.TryRemove(approvalId, out _)) throw new InvalidOperationException("审批已被处理。");
        emit(runId, "approval.resolved", new { approvalId, approved, message = approved ? "已批准操作。" : "已拒绝操作。" });
        item.Completion.TrySetResult(approved);
        return new { accepted = true };
    }
}

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Niuery.Agent.Runtime.Tests;

// 仅用于离线验证 MAF 集成契约，不能作为真实模型验收证据。
internal sealed class ScriptedChatClient : IChatClient
{
    public List<ChatMessage> LastMessages { get; private set; } = [];
    public int RequestCount { get; private set; }
    public bool BlockUntilCancelled { get; init; }
    public string ToolName { get; init; } = "VerifyConnection";
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        RequestCount++;
        Entered.TrySetResult();
        if (BlockUntilCancelled) await Task.Delay(Timeout.Infinite, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var previous = LastMessages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        return previous is null
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("test-call-1", ToolName, new Dictionary<string, object?>())]))
                { FinishReason = ChatFinishReason.ToolCalls, ResponseId = Guid.NewGuid().ToString() }
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, $"工具返回：{previous.Result}"))
                { FinishReason = ChatFinishReason.Stop, ResponseId = Guid.NewGuid().ToString() };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

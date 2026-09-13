using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Niuery.Agent.Runtime.Maf;
using Xunit;

namespace Niuery.Agent.Runtime.Tests;

public sealed class HarnessTests
{
    [Fact(DisplayName = "编码 Harness 默认使用计划模式并支持切换")]
    public async Task CodingHarnessUsesPlanModeByDefault()
    {
        using var client = new ScriptedChatClient();
        var agent = HarnessFactory.CreateCoding(client, []);
        var session = await agent.CreateSessionAsync();
        var provider = agent.GetService<AgentModeProvider>();

        Assert.NotNull(provider);
        Assert.Equal("plan", await provider!.GetModeAsync(session));
        await provider.SetModeAsync(session, "execute");
        Assert.Equal("execute", await provider.GetModeAsync(session));
    }

    [Fact(DisplayName = "MAF 流式循环执行真实本地函数并将结果传回模型接口")]
    public async Task StreamingInvokesToolAndReturnsResult()
    {
        using var client = new ScriptedChatClient();
        var invocations = 0;
        string VerifyConnection() { invocations++; return "本地工具证据"; }
        var agent = HarnessFactory.CreateProbe(client, [AIFunctionFactory.Create(VerifyConnection, "VerifyConnection")]);
        var session = await agent.CreateSessionAsync();
        var output = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync("验证工具。", session)) output.Append(update.Text);
        Assert.Equal(1, invocations);
        Assert.Equal(2, client.RequestCount);
        Assert.Contains("本地工具证据", output.ToString());
        Assert.Contains(client.LastMessages.SelectMany(m => m.Contents), c => c is FunctionResultContent);
    }

    [Fact(DisplayName = "会话序列化恢复保留历史且不重新执行已有工具")]
    public async Task SessionRoundTripPreservesToolHistory()
    {
        using var client = new ScriptedChatClient();
        var invocations = 0;
        string VerifyConnection() { invocations++; return "恢复证据"; }
        var tool = AIFunctionFactory.Create(VerifyConnection, "VerifyConnection");
        var agent = HarnessFactory.CreateProbe(client, [tool]);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("验证工具。", session);
        var snapshot = await agent.SerializeSessionAsync(session);
        using var secondClient = new ScriptedChatClient();
        var secondAgent = HarnessFactory.CreateProbe(secondClient, [tool]);
        var restored = await secondAgent.DeserializeSessionAsync(snapshot);
        var response = await secondAgent.RunAsync("继续。", restored);
        Assert.Equal(1, invocations);
        Assert.Contains("恢复证据", response.Text);
        Assert.Contains(secondClient.LastMessages, m => m.Role == ChatRole.User && m.Text == "验证工具。");
    }

    [Fact(DisplayName = "执行中的取消令牌传到模型接口且取消后不调用工具")]
    public async Task CancellationReachesClient()
    {
        using var client = new ScriptedChatClient { BlockUntilCancelled = true };
        var invocations = 0;
        string VerifyConnection() { invocations++; return "不应执行"; }
        var agent = HarnessFactory.CreateProbe(client, [AIFunctionFactory.Create(VerifyConnection, "VerifyConnection")]);
        using var cancellation = new CancellationTokenSource();
        var running = Task.Run(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync("取消测试。", cancellationToken: cancellation.Token)) { }
        });
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, invocations);
    }

    [Fact(DisplayName = "需要审批的函数不会在未批准时执行")]
    public async Task ApprovalRequiredToolDoesNotExecute()
    {
        using var client = new ScriptedChatClient();
        var invocations = 0;
        string VerifyConnection() { invocations++; return "不应执行"; }
        var tool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(VerifyConnection, "VerifyConnection"));
        var agent = HarnessFactory.CreateProbe(client, [tool]);
        var response = await agent.RunAsync("请求受控工具。");
        Assert.Equal(0, invocations);
        Assert.Contains(response.Messages.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent);
    }

    [Theory(DisplayName = "审批状态可序列化恢复且批准与拒绝分别生效")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApprovalRoundTripHonorsDecision(bool approved)
    {
        using var client = new ScriptedChatClient();
        var invocations = 0;
        string VerifyConnection() { invocations++; return "已批准的工具结果"; }
        var tool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(VerifyConnection, "VerifyConnection"));
        var agent = HarnessFactory.CreateProbe(client, [tool]);
        var session = await agent.CreateSessionAsync();
        var initial = await agent.RunAsync("申请工具。", session);
        var request = Assert.Single(initial.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());
        var snapshot = await agent.SerializeSessionAsync(session);
        using var nextClient = new ScriptedChatClient();
        var nextAgent = HarnessFactory.CreateProbe(nextClient, [tool]);
        var restored = await nextAgent.DeserializeSessionAsync(snapshot);
        var response = await nextAgent.RunAsync(new ChatMessage(ChatRole.User,
            [request.CreateResponse(approved, approved ? "用户批准本次调用" : "用户拒绝本次调用")]), restored);
        Assert.Equal(approved ? 1 : 0, invocations);
        if (approved) Assert.Contains("已批准的工具结果", response.Text);
    }
}

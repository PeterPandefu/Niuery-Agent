using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Niuery.Agent.Runtime.Maf;

Console.OutputEncoding = Encoding.UTF8;
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};
if (args.Length != 3 || args[0] != "probe")
{
    Console.Error.WriteLine("用法：dotnet run --project src/Niuery.Agent.Diagnostics -- probe <配置文件路径> <提供商编号>");
    return 2;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var clock = Stopwatch.StartNew();
var toolCalls = 0;
var textUpdates = 0;
var nonce = Guid.NewGuid().ToString("N");
try
{
    var configurations = await ProviderConfiguration.LoadAsync(args[1], shutdown.Token);
    var configuration = configurations.SingleOrDefault(p => p.Id == args[2])
        ?? throw new ConfigurationException("找不到指定的提供商编号。");
    configuration.Validate();
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
    timeout.CancelAfter(TimeSpan.FromSeconds(configuration.TimeoutSeconds));
    using var client = HarnessFactory.CreateClient(configuration);

    [Description("返回仅本地工具知道的随机验证字符串。验证连接时必须调用此工具，不能自行猜测结果。")]
    string VerifyConnection()
    {
        Interlocked.Increment(ref toolCalls);
        return nonce;
    }

    var agent = HarnessFactory.CreateProbe(client, [AIFunctionFactory.Create(VerifyConnection, "VerifyConnection")]);
    var session = await agent.CreateSessionAsync(timeout.Token);
    var response = new StringBuilder();
    await foreach (var update in agent.RunStreamingAsync(
        "调用 VerifyConnection 工具，然后用中文简短说明连接已验证，并原样附上工具返回的字符串。", session, cancellationToken: timeout.Token))
    {
        if (!string.IsNullOrEmpty(update.Text))
        {
            textUpdates++;
            response.Append(update.Text);
        }
    }
    if (toolCalls == 0 || !response.ToString().Contains(nonce, StringComparison.Ordinal) || textUpdates == 0)
        throw new ProbeException("模型未完整执行工具调用和流式结果返回，验收未通过。");

    var snapshot = await agent.SerializeSessionAsync(session, cancellationToken: timeout.Token);
    var restored = await agent.DeserializeSessionAsync(snapshot, cancellationToken: timeout.Token);
    var before = toolCalls;
    var continued = await agent.RunAsync("不要再次调用工具。请原样重复上一轮工具返回的验证字符串。", restored, cancellationToken: timeout.Token);
    if (!continued.Text.Contains(nonce, StringComparison.Ordinal) || toolCalls != before)
        throw new ProbeException("会话恢复后未正确保留上下文，或重复调用了工具。");

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        type = "probe.completed", message = "真实模型流式输出、工具调用与会话恢复验收通过。",
        providerId = configuration.Id, configuration.Model, toolCalls, textUpdates,
        sessionRestored = true, elapsedMilliseconds = clock.ElapsedMilliseconds
    }, jsonOptions));
    return 0;
}
catch (ConfigurationException ex)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "probe.blocked", message = ex.Message }, jsonOptions));
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "probe.cancelled", message = "验收已取消或达到配置时限。" }, jsonOptions));
    return 3;
}
catch (ProbeException ex)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "probe.failed", message = ex.Message }, jsonOptions));
    return 1;
}
catch (System.ClientModel.ClientResultException ex)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        type = "probe.failed", status = ex.Status,
        message = "模型服务拒绝或未完成请求，请核对密钥权限、模型可用性和接口地址。"
    }, jsonOptions));
    return 1;
}
catch (Exception)
{
    // 不输出上游异常原文，避免服务响应、请求地址或凭证进入日志。
    Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "probe.failed", message = "连接验收失败，请检查配置文件、服务状态、模型权限和网络连接。" }, jsonOptions));
    return 1;
}

internal sealed class ProbeException(string message) : Exception(message);

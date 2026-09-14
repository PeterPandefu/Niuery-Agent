using Niuery.Agent.Runtime.Maf;
using Xunit;

namespace Niuery.Agent.Runtime.Tests;

public sealed class ProviderConfigurationTests
{
    private static ProviderConfiguration Valid => new("test", "openai-compatible", "https://models.example.org/v1/", "configured-model", "test-api-key");

    [Theory(DisplayName = "拒绝不安全或夹带凭证的服务地址")]
    [InlineData("http://models.example.org/v1/")]
    [InlineData("https://user:secret@models.example.org/v1/")]
    [InlineData("https://models.example.org/v1/?key=secret")]
    [InlineData("file:///tmp/model")]
    [InlineData("https://models.example.org/#secret")]
    public void RejectUnsafeEndpoints(string url) => Assert.Throws<ConfigurationException>(() => (Valid with { BaseUrl = url }).Validate());

    [Fact(DisplayName = "允许本机 Ollama 无凭证连接")]
    public void AllowLoopbackOllama()
    {
        var config = Valid with { Kind = "ollama", BaseUrl = "http://127.0.0.1:11434/v1/", ApiKey = null };
        Assert.True(config.Validate().IsLoopback);
        Assert.Equal("ollama", config.ResolveApiKey());
    }

    [Fact(DisplayName = "缺少 API Key 明确报错且不泄露值")]
    public void MissingApiKeyIsActionable()
    {
        var config = Valid with { ApiKey = "" };
        var error = Assert.Throws<ConfigurationException>(() => config.ResolveApiKey());
        Assert.Contains("API Key", error.Message);
    }

    [Fact(DisplayName = "示例模型不能误报为真实配置")]
    public void RejectPlaceholderModel() => Assert.Throws<ConfigurationException>(() => (Valid with { Model = "请填写实际模型名" }).Validate());

    [Fact(DisplayName = "不支持工具调用的模型不能通过编码验收")]
    public void RejectMissingTools() => Assert.Throws<ConfigurationException>(() => (Valid with { SupportsTools = false }).Validate());

    [Fact(DisplayName = "Responses 传输协议可以创建 MAF ChatClient")]
    public void ResponsesTransportCreatesChatClient()
    {
        var config = Valid with { Transport = "responses", ReasoningOutput = "summary" };
        using var client = HarnessFactory.CreateClient(config);
        Assert.NotNull(client);
    }

    [Fact(DisplayName = "缺少配置文件时探测给出中文错误且不泄露路径")]
    public async Task MissingFileThrowsChinese()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "providers.local.json");
        var error = await Assert.ThrowsAsync<ConfigurationException>(() => ProviderConfiguration.LoadAsync(path));
        Assert.Contains("找不到模型配置文件", error.Message);
        Assert.DoesNotContain(path, error.Message);
    }

    [Fact(DisplayName = "缺少配置文件时执行器按空列表启动")]
    public async Task MissingFileLoadsAsEmptyForWorker()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "providers.local.json");
        var providers = await ProviderConfiguration.LoadOrEmptyAsync(path);
        Assert.Empty(providers);
    }
}

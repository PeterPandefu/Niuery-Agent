using Niuery.Agent.Runtime.Maf;
using Xunit;

namespace Niuery.Agent.Runtime.Tests;

public sealed class ProviderConfigurationTests
{
    private static ProviderConfiguration Valid => new("test", "openai-compatible", "https://models.example.org/v1/", "configured-model", "NIUERY_TEST_MISSING_KEY");

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
        var config = Valid with { Kind = "ollama", BaseUrl = "http://127.0.0.1:11434/v1/", ApiKeyEnvironmentVariable = null };
        Assert.True(config.Validate().IsLoopback);
        Assert.Equal("ollama", config.ResolveApiKey());
    }

    [Fact(DisplayName = "缺少凭证明确报错且不泄露值")]
    public void MissingCredentialIsActionable()
    {
        var config = Valid with { ApiKeyEnvironmentVariable = $"NIUERY_MISSING_{Guid.NewGuid():N}" };
        var error = Assert.Throws<ConfigurationException>(() => config.ResolveApiKey());
        Assert.Contains("环境变量", error.Message);
    }

    [Fact(DisplayName = "示例模型不能误报为真实配置")]
    public void RejectPlaceholderModel() => Assert.Throws<ConfigurationException>(() => (Valid with { Model = "请填写实际模型名" }).Validate());

    [Fact(DisplayName = "不支持工具调用的模型不能通过编码验收")]
    public void RejectMissingTools() => Assert.Throws<ConfigurationException>(() => (Valid with { SupportsTools = false }).Validate());
}

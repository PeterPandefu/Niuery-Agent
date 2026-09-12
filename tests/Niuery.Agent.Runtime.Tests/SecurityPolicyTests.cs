using Niuery.Agent.Worker;
using Xunit;
namespace Niuery.Agent.Runtime.Tests;
public sealed class SecurityPolicyTests
{
    [Theory(DisplayName = "日志脱敏不保留常见凭证字段")]
    [InlineData("api_key=abc123")]
    [InlineData("Authorization: Bearer secret-token")]
    [InlineData("password = p@ss")]
    public void RedactsSecrets(string input) { var value=SecurityPolicy.Redact(input); Assert.DoesNotContain("abc123",value); Assert.DoesNotContain("secret-token",value); Assert.DoesNotContain("p@ss",value); Assert.Contains("已隐藏",value); }
    [Fact(DisplayName = "拒绝超出执行预算")]
    public void RejectsBudget() { Assert.Throws<InvalidOperationException>(()=>SecurityPolicy.ValidateBudget(101,0,TimeSpan.Zero)); Assert.Throws<InvalidOperationException>(()=>SecurityPolicy.ValidateBudget(0,2_000_001,TimeSpan.Zero)); }
}

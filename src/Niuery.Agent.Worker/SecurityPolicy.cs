using System.Text.RegularExpressions;

namespace Niuery.Agent.Worker;

public static partial class SecurityPolicy
{
    [GeneratedRegex("(?i)(api[_-]?key|authorization|bearer|password|secret|token)\\s*[:=]\\s*[^\\s,;]+|(?i)(authorization|bearer)\\s+[^\\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
    public static string Redact(string value)
    {
        var redacted = System.Text.RegularExpressions.Regex.Replace(value, "(?i)(authorization|bearer)\\s*[:=]?\\s*[^\\r\\n,;]+", "$1 [已隐藏]");
        return SecretPattern().Replace(redacted, "$1=[已隐藏]");
    }
    public static bool IsSafeLogValue(string value) => !SecretPattern().IsMatch(value);
    public static void ValidateBudget(int iterations, int outputChars, TimeSpan elapsed)
    {
        if (iterations > 100) throw new InvalidOperationException("单次执行超过工具调用预算。");
        if (outputChars > 2_000_000) throw new InvalidOperationException("单次执行输出超过预算。");
        if (elapsed > TimeSpan.FromMinutes(15)) throw new InvalidOperationException("单次执行超过时间预算。");
    }
}

using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class LearningTestPromptFactoryTests
{
    [Fact]
    public void Create_SendsOnlyLowercaseMessageAndBothPolicySets()
    {
        var text = new ChatTextNormalizer().Process("KEKW ÄRGER");
        var policy = new EffectivePolicySnapshot(
            [
                new(PolicySetCode.General, 2),
                new(PolicySetCode.Backseatgaming, 3),
            ],
            "policy")
        {
            ActiveRules =
            [
                Rule(ModerationRuleCodes.OtherAbuse, PolicySetCode.General),
                Rule(
                    ModerationRuleCodes.Backseatgaming,
                    PolicySetCode.Backseatgaming),
            ],
        };

        var result = LearningTestPromptFactory.Create(
            Guid.NewGuid(),
            text,
            policy,
            []);

        Assert.Equal("kekw ärger", result.Envelope.Message.LowerText);
        using var json = System.Text.Json.JsonDocument.Parse(result.UserPrompt);
        Assert.Equal(
            "kekw ärger",
            json.RootElement
                .GetProperty("message")
                .GetProperty("lowerText")
                .GetString());
        Assert.DoesNotContain("KEKW", result.UserPrompt, StringComparison.Ordinal);
        Assert.Equal(
            [PolicySetCode.General, PolicySetCode.Backseatgaming],
            result.Envelope.ActivePolicySets.Select(item => item.Code));
        Assert.DoesNotContain("OriginalText", result.UserPrompt, StringComparison.Ordinal);
        Assert.Contains(
            "use a 600-second timeout as the normal baseline",
            result.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "delete only for a single low-context, unnecessary nuisance message",
            result.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never choose delete for insults, harassment, hate, threats",
            result.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Every moderate G_ADVERTISING decision must use ban",
            result.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Every moderate G_BEGGING decision must use a 86400-second timeout",
            result.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "call them \"Nutzer oder Streamer\", never only \"Nutzer\"",
            result.SystemPrompt,
            StringComparison.Ordinal);
    }

    private static ModerationRuleDefinition Rule(
        string code,
        PolicySetCode policySet) =>
        new(
            code,
            policySet,
            code,
            "rule",
            ModerationActionKind.Timeout,
            600,
            "Regelverstoß.",
            100);
}

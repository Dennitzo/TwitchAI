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
                .GetProperty("text")
                .GetString());
        Assert.DoesNotContain("KEKW", result.UserPrompt, StringComparison.Ordinal);
        Assert.Equal(
            [PolicySetCode.General, PolicySetCode.Backseatgaming],
            result.Envelope.ActivePolicySets.Select(item => item.Code));
        Assert.DoesNotContain("OriginalText", result.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("activePolicySets", result.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("defaultAction", result.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("publicReason", result.UserPrompt, StringComparison.Ordinal);
        Assert.Contains(
            "use timeout 600 for ordinary abuse",
            result.SystemPrompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Delete only a single low-context nuisance",
            result.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "G_ADVERTISING self-promotion or promotion of another channel always uses ban",
            result.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "86400 for strong insults or G_BEGGING",
            result.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "say \"Nutzer oder Streamer\", never only \"Nutzer\"",
            result.SystemPrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "One through nineteen emotes alone are none",
            result.SystemPrompt,
            StringComparison.Ordinal);
        Assert.True(result.PromptCharacters.System > 0);
        Assert.True(result.PromptCharacters.Rules > 0);
        Assert.True(result.PromptCharacters.Messages > 0);
        Assert.Equal(0, result.PromptCharacters.Examples);
        Assert.Equal(0, result.PromptCharacters.Templates);
        Assert.Equal(0, result.PromptCharacters.Schema);
    }

    [Fact]
    public void Create_SerializesActiveRuleCriteriaOnceAndOmitsEmptySections()
    {
        var text = new ChatTextNormalizer().Process("hello");
        var policy = new EffectivePolicySnapshot(
            [new(PolicySetCode.General, 1)],
            "policy")
        {
            ActiveRules =
            [
                new ModerationRuleDefinition(
                    ModerationRuleCodes.Spam,
                    PolicySetCode.General,
                    "Spam",
                    ModerationRuleCodes.SpamPromptInstruction,
                    ModerationActionKind.Timeout,
                    120,
                    "Spam."),
            ],
        };

        var result = LearningTestPromptFactory.Create(
            Guid.NewGuid(),
            text,
            policy,
            []);

        Assert.Equal(
            1,
            CountOccurrences(result.UserPrompt, ModerationRuleCodes.SpamPromptInstruction));
        Assert.DoesNotContain(
            ModerationRuleCodes.SpamPromptInstruction,
            result.SystemPrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain("localMatchCodes", result.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("learningExamples", result.UserPrompt, StringComparison.Ordinal);
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

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}

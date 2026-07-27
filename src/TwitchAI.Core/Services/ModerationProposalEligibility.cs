using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public static class ModerationProposalEligibility
{
    public static bool CanBeReviewed(ValidatedModerationDecision? decision)
    {
        return decision is not null
            && IsActionable(decision.Action)
            && ModerationActionRules.IsValid(
                decision.Action,
                decision.TimeoutSeconds)
            && decision.PolicySet is { } policySet
            && Enum.IsDefined(policySet)
            && !string.IsNullOrWhiteSpace(decision.RuleCode);
    }

    public static bool CanBeReviewed(ModerationCase? moderationCase)
    {
        return moderationCase is not null
            && IsActionable(moderationCase.ProposedAction)
            && ModerationActionRules.IsValid(
                moderationCase.ProposedAction,
                moderationCase.ProposedTimeoutSeconds)
            && Enum.IsDefined(moderationCase.PolicySet)
            && !string.IsNullOrWhiteSpace(moderationCase.RuleCode);
    }

    private static bool IsActionable(ModerationActionKind action) =>
        action is ModerationActionKind.Delete or ModerationActionKind.Timeout;
}

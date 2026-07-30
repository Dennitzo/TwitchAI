using System.Globalization;

namespace TwitchAI.Infrastructure.Persistence;

internal static class LearningPolicyRevisionFingerprint
{
    public static string Create(EffectivePolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"v1:g={policy.GeneralRevision};b={policy.CategoryRevision};h={policy.PolicyHash}");
    }
}

using System.Security.Cryptography;
using System.Text;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed class EffectivePolicyResolver(
    string justChattingCategoryId = EffectivePolicyResolver.BootstrapJustChattingCategoryId,
    string irlCategoryId = EffectivePolicyResolver.BootstrapIrlCategoryId,
    IModerationPolicyRuntimeSettings? runtimeSettings = null)
    : IEffectivePolicyResolver
{
    public const string BootstrapJustChattingCategoryId = "509658";
    public const string BootstrapIrlCategoryId = "509672";

    public EffectivePolicySnapshot Resolve(
        ChannelContextSnapshot channelContext,
        long generalRevision,
        long backseatgamingRevision)
    {
        ArgumentNullException.ThrowIfNull(channelContext);
        ArgumentOutOfRangeException.ThrowIfLessThan(generalRevision, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(backseatgamingRevision, 1);

        var revisions = new List<PolicySetRevision>
        {
            new(PolicySetCode.General, generalRevision),
        };

        if ((runtimeSettings?.BackseatgamingEnabled ?? true)
            && channelContext.HasKnownCategory
            && !string.Equals(
                channelContext.CategoryId,
                justChattingCategoryId,
                StringComparison.Ordinal)
            && !string.Equals(
                channelContext.CategoryId,
                irlCategoryId,
                StringComparison.Ordinal))
        {
            revisions.Add(
                new PolicySetRevision(
                    PolicySetCode.Backseatgaming,
                    backseatgamingRevision));
        }

        var canonical = string.Join(
            "|",
            revisions.Select(item => $"{item.Code}:{item.Revision}"));
        var fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        return new EffectivePolicySnapshot(revisions, fingerprint);
    }
}

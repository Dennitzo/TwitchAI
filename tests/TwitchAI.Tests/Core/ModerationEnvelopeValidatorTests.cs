using System.Text.Json;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class ModerationEnvelopeValidatorTests
{
    [Fact]
    public void ValidateLive_AutoModHeldMessageAcceptsNoActionWithoutAllowMatch()
    {
        var batch = AutoModBatch("friendly blocked phrase", "blocked_term");
        var response = V7Response(
            batch,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["m1"] = null,
            });

        var result = new ModerationEnvelopeValidator().ValidateLive(response, batch);

        Assert.True(result.IsValid);
        Assert.Equal(
            ModerationActionKind.None,
            Assert.Single(result.ValidDecisions).Action);
    }

    [Fact]
    public void ValidateLive_AutoModHeldMessageRejectsDeleteDecision()
    {
        var batch = AutoModBatch("you are an idiot", "automod");
        var response = V7Response(
            batch,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["m1"] = V11Moderation(
                    "delete",
                    null,
                    ModerationRuleCodes.PersonalAttack,
                    "direct_insult",
                    "Direkte persönliche Beleidigung eines Nutzers oder des Streamers.",
                    ["you are an idiot"],
                    0.98),
            });

        var result = new ModerationEnvelopeValidator().ValidateLive(
            response,
            batch);

        Assert.False(result.IsValid);
        Assert.Empty(result.ValidDecisions);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "automod_action_invalid");
        Assert.Equal(["m1"], result.RepairMessageIds);
    }

    [Fact]
    public void ValidateLive_AutoModHeldMessageAcceptsTextSupportedActiveRule()
    {
        var baseBatch = PersonalAttackBatch("du bist völlig unfähig");
        var source = baseBatch.Messages[0];
        var batch = baseBatch with
        {
            Messages =
            [
                source with
                {
                    Message = source.Message with
                    {
                        IsAutoModHeld = true,
                        AutoModReason = "automod",
                        AutoModCategory = "aggressive",
                        AutoModLevel = 3,
                    },
                },
            ],
        };
        var response = V7Response(
            batch,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["m1"] = V11Moderation(
                    "timeout",
                    600,
                    ModerationRuleCodes.PersonalAttack,
                    "direct_insult",
                    "Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers.",
                    ["du bist völlig unfähig"],
                    0.98),
            });

        var result = new ModerationEnvelopeValidator().ValidateLive(
            response,
            batch);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.ValidDecisions);
        Assert.Equal(ModerationRuleCodes.PersonalAttack, decision.RuleCode);
        Assert.Equal(ModerationActionKind.Timeout, decision.Action);
        Assert.Empty(result.RepairMessageIds);
    }

    [Fact]
    public void ValidateLive_AutoModBlockedTermCannotBypassSpamEvidence()
    {
        var batch = AutoModBatch(
            "Ich krieg momentan nicht mal 15 am Stück hin, Respekt",
            "blocked_term");
        var response = V7Response(
            batch,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["m1"] = V11Moderation(
                    "timeout",
                    120,
                    ModerationRuleCodes.Spam,
                    "emote_flood",
                    "Unbelegtes Flooding im Chat.",
                    [batch.Messages[0].Message.Text.LowerText],
                    0.98),
            });

        var result = new ModerationEnvelopeValidator().ValidateLive(
            response,
            batch);

        Assert.False(result.IsValid);
        Assert.Empty(result.ValidDecisions);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "spam_signal_missing");
        Assert.Equal(["m1"], result.RepairMessageIds);
    }

    [Theory]
    [InlineData("Gezielte persönliche Herabsetzung von @Mayvo.")]
    [InlineData("Gezielte persönliche Herabsetzung von Mayvo.")]
    [InlineData("G_PERSONAL_ATTACK wegen gezielter persönlicher Herabsetzung.")]
    [InlineData("Targeted harassment against a user.")]
    public void ValidateLive_InvalidUserFacingReasonRequiresTargetedRepair(
        string reason)
    {
        var batch = PersonalAttackBatch("@mayvo du bist völlig unfähig");
        var response = V7Response(
            batch,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["m1"] = V11Moderation(
                    "timeout",
                    600,
                    ModerationRuleCodes.PersonalAttack,
                    "direct_insult",
                    reason,
                    ["@mayvo du bist völlig unfähig"],
                    0.98),
            });

        var result = new ModerationEnvelopeValidator().ValidateLive(
            response,
            batch);

        Assert.False(result.IsValid);
        Assert.Empty(result.ValidDecisions);
        Assert.Equal(["m1"], result.RepairMessageIds);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "reason_invalid"
                && issue.MessageId == "m1");
    }

    [Fact]
    public void ValidateLive_RejectsCopiedSpamDecisionForEveryOrdinaryBatchMessage()
    {
        string[] ordinaryMessages =
        [
            "Jaaa <3",
            "Wäre ich dabei",
            "tschüssi @chat, @mods und shlo",
            "uhh geilo",
            "ok wo anmeldung",
            "und 1 Shloschlik",
            "Geil Bin dabei KEKW",
        ];
        var batch = SpamBatch(ordinaryMessages);
        var decisions = batch.Messages.ToDictionary(
            message => message.TemporaryMessageId,
            message => (object?)V11Moderation(
                "delete",
                null,
                ModerationRuleCodes.Spam,
                "text_flood",
                "Unbelegtes Flooding im Chat.",
                [message.Message.Text.LowerText],
                0.8),
            StringComparer.Ordinal);

        var result = new ModerationEnvelopeValidator().ValidateLive(
            V7Response(batch, decisions),
            batch);

        Assert.False(result.IsValid);
        Assert.Empty(result.ValidDecisions);
        Assert.Equal(
            batch.Messages.Select(message => message.TemporaryMessageId),
            result.RepairMessageIds);
        Assert.Equal(ordinaryMessages.Length, result.Issues.Count);
        Assert.All(
            result.Issues,
            issue => Assert.Equal("spam_signal_missing", issue.Code));
    }

    [Fact]
    public void ValidateLive_AcceptsRepeatedPhraseSpamForItsOwnMessageOnly()
    {
        const string repeated =
            "i cant stop laughing megalul i cant stop laughing megalul "
            + "i cant stop laughing megalul i cant stop laughing megalul "
            + "i cant stop laughing megalul";
        var batch = SpamBatch(repeated);
        var response = V7Response(
            batch,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["m1"] = V11Moderation(
                    "delete",
                    null,
                    ModerationRuleCodes.Spam,
                    "text_flood",
                    "Mehrfach wiederholte Wortfolge im Chat.",
                    ["i cant stop laughing megalul"],
                    0.97),
            });

        var result = new ModerationEnvelopeValidator().ValidateLive(
            response,
            batch);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.ValidDecisions);
        Assert.Equal(ModerationRuleCodes.Spam, decision.RuleCode);
        Assert.Empty(result.RepairMessageIds);
    }

    [Fact]
    public void ValidateLive_AcceptsStructuredManualSpamPhraseAsTextEvidence()
    {
        var baseBatch = SpamBatch("limited time offer now");
        var source = baseBatch.Messages[0];
        var batch = baseBatch with
        {
            Messages =
            [
                source with
                {
                    LocalMatches =
                    [
                        new BlacklistMatch(
                            17,
                            ModerationRuleCodes.Spam,
                            PolicySetCode.General,
                            "limited time offer"),
                    ],
                },
            ],
        };
        var response = V7Response(
            batch,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["m1"] = V11Moderation(
                    "delete",
                    null,
                    ModerationRuleCodes.Spam,
                    "text_flood",
                    "Wiederholtes Werbemuster im Chat.",
                    ["limited time offer"],
                    0.95),
            });

        var result = new ModerationEnvelopeValidator().ValidateLive(
            response,
            batch);

        Assert.True(result.IsValid);
        Assert.Equal(
            ModerationRuleCodes.Spam,
            Assert.Single(result.ValidDecisions).RuleCode);
    }

    [Fact]
    public void Validate_ValidOneToOneEnvelopeReturnsTypedDecisions()
    {
        var batch = Batch(
            2,
            includeBackseat: true,
            firstMessage: "fahr links, dort ist eine falle");
        var response = Response(
            batch,
            Decision("m1", "timeout", 600, "B", "backseat", ["fahr links"], 0.91),
            Decision("m2", "none", null, null, "", [], 0.12));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Envelope);
        Assert.Equal(2, result.Envelope.Decisions.Count);
        Assert.Equal(ModerationActionKind.Timeout, result.Envelope.Decisions[0].Action);
        Assert.Equal(PolicySetCode.Backseatgaming, result.Envelope.Decisions[0].PolicySet);
        Assert.Equal(ModerationRuleCodes.Backseatgaming, result.Envelope.Decisions[0].RuleCode);
        Assert.Equal(600, result.Envelope.Decisions[0].TimeoutSeconds);
    }

    [Fact]
    public void Validate_MissingDecisionRejectsWholeBatch()
    {
        var batch = Batch(2, includeBackseat: false);
        var response = Response(
            batch,
            Decision("m1", "delete", null, "G", "spam", ["bad"], 0.9));

        AssertInvalid(response, batch, "decision_set_incomplete");
    }

    [Fact]
    public void Validate_DuplicateMessageIdRejectsWholeBatch()
    {
        var batch = Batch(2, includeBackseat: false);
        var response = Response(
            batch,
            Decision("m1", "delete", null, "G", "spam", ["bad"], 0.9),
            Decision("m1", "none", null, null, "", [], 0.1));

        AssertInvalid(response, batch, "message_id_duplicate");
    }

    [Fact]
    public void Validate_UnknownMessageIdRejectsWholeBatch()
    {
        var batch = Batch(1, includeBackseat: false);
        var response = Response(
            batch,
            Decision("unknown", "none", null, null, "", [], 0.1));

        AssertInvalid(response, batch, "message_id_unknown");
    }

    [Fact]
    public void Validate_MissingOrUnknownPropertyRejectsWholeBatch()
    {
        var batch = Batch(1, includeBackseat: false);
        var missing = Decision("m1", "none", null, null, "", [], 0.1);
        missing.Remove("messageId");
        AssertInvalid(Response(batch, missing), batch, "property_missing");

        var unknown = Decision("m1", "none", null, null, "", [], 0.1);
        unknown["username"] = "model-selected-user";
        AssertInvalid(Response(batch, unknown), batch, "property_unknown");
    }

    [Fact]
    public void Validate_BackseatPolicyWhenInactiveRejectsWholeBatch()
    {
        var batch = Batch(1, includeBackseat: false);
        var response = Response(
            batch,
            Decision("m1", "delete", null, "B", "backseat", ["spoiler"], 0.9));

        AssertInvalid(response, batch, "policy_inactive");
    }

    [Theory]
    [InlineData("zorro")]
    [InlineData("nichts")]
    public void Validate_BackseatSingleWordFragmentRejectsWholeBatch(string message)
    {
        var batch = Batch(1, includeBackseat: true, firstMessage: message);
        var response = Response(
            batch,
            Decision("m1", "timeout", 600, "B", "backseat", [message], 0.9));

        AssertInvalid(response, batch, "backseat_context_insufficient");
    }

    [Fact]
    public void Validate_BackseatQuestionRejectsWholeBatch()
    {
        var batch = Batch(
            1,
            includeBackseat: true,
            firstMessage: "soll ich links gehen?");
        var response = Response(
            batch,
            Decision(
                "m1",
                "timeout",
                600,
                "B",
                "backseat",
                ["soll ich links gehen"],
                0.9));

        AssertInvalid(response, batch, "backseat_context_insufficient");
    }

    [Fact]
    public void Validate_BackseatRequiresConcreteMultiWordEvidence()
    {
        var batch = Batch(
            1,
            includeBackseat: true,
            firstMessage: "fahr links, dort ist eine falle");
        var response = Response(
            batch,
            Decision("m1", "timeout", 600, "B", "backseat", ["links"], 0.9));

        AssertInvalid(response, batch, "backseat_evidence_insufficient");
    }

    [Fact]
    public void Validate_GeneralSingleWordViolationRemainsValid()
    {
        var batch = Batch(1, includeBackseat: false, firstMessage: "idiot");
        var response = Response(
            batch,
            Decision(
                "m1",
                "delete",
                null,
                "G",
                "Direkte Beleidigung.",
                ["idiot"],
                0.9,
                "G_INSULT"));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Envelope);
        var decision = Assert.Single(result.Envelope.Decisions);
        Assert.Equal("G_INSULT", decision.RuleCode);
        Assert.Equal(ModerationActionKind.Timeout, decision.Action);
        Assert.Equal(600, decision.TimeoutSeconds);
    }

    [Fact]
    public void Validate_NoneForBackseatQuestionRemainsValid()
    {
        var batch = Batch(
            1,
            includeBackseat: true,
            firstMessage: "soll ich links gehen?");
        var response = Response(
            batch,
            Decision("m1", "none", null, null, "", [], 0.8));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        Assert.Equal(
            ModerationActionKind.None,
            Assert.Single(result.Envelope!.Decisions).Action);
    }

    [Fact]
    public void Validate_KnownReplyTemplateForNoneReturnsTypedReference()
    {
        var batch = Batch(1, includeBackseat: false) with
        {
            ReplyTemplates =
            [
                new ChatReplyTemplatePrompt(
                    "r1",
                    "wie lange geht das event noch?"),
            ],
        };
        var response = Response(
            batch,
            Decision(
                "m1",
                "none",
                null,
                null,
                "",
                [],
                0.1,
                replyTemplateId: "r1",
                replyConfidence: 0.94));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal("r1", decision.ReplyTemplateId);
        Assert.Equal(0.94, decision.ReplyConfidence);
    }

    [Fact]
    public void Validate_UnknownReplyTemplateRejectsWholeBatch()
    {
        var batch = Batch(1, includeBackseat: false);
        var response = Response(
            batch,
            Decision(
                "m1",
                "none",
                null,
                null,
                "",
                [],
                0.1,
                replyTemplateId: "unknown",
                replyConfidence: 0.8));

        AssertInvalid(response, batch, "reply_template_unknown");
    }

    [Fact]
    public void Validate_ModerationActionCannotAlsoSelectReply()
    {
        var batch = Batch(1, includeBackseat: false) with
        {
            ReplyTemplates =
            [
                new ChatReplyTemplatePrompt("r1", "event"),
            ],
        };
        var response = Response(
            batch,
            Decision(
                "m1",
                "delete",
                null,
                "G",
                "Spam.",
                ["bad"],
                0.9,
                replyTemplateId: "r1",
                replyConfidence: 0.8));

        AssertInvalid(response, batch, "reply_for_moderation_action");
    }

    [Fact]
    public void Validate_BanWithoutDurationReturnsTypedDecision()
    {
        var batch = Batch(
            1,
            includeBackseat: false,
            firstMessage: "du bist ein extremer idiot");
        var response = Response(
            batch,
            Decision(
                "m1",
                "ban",
                null,
                "G",
                "Eindeutig extreme Beleidigung.",
                ["extremer idiot"],
                0.99,
                "G_INSULT"));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal(ModerationActionKind.Ban, decision.Action);
        Assert.Null(decision.TimeoutSeconds);
        Assert.Equal(PolicySetCode.General, decision.PolicySet);
        Assert.Equal("G_INSULT", decision.RuleCode);
        Assert.Equal("Eindeutig extreme Beleidigung.", decision.Reason);
    }

    [Fact]
    public void Validate_BanCannotSelectReplyTemplate()
    {
        var batch = Batch(1, includeBackseat: false) with
        {
            ReplyTemplates =
            [
                new ChatReplyTemplatePrompt("r1", "event"),
            ],
        };
        var response = Response(
            batch,
            Decision(
                "m1",
                "ban",
                null,
                "G",
                "Eindeutig extreme Beleidigung.",
                ["bad"],
                0.99,
                "G_INSULT",
                replyTemplateId: "r1",
                replyConfidence: 0.8));

        AssertInvalid(response, batch, "reply_for_moderation_action");
    }

    [Fact]
    public void Validate_BanForSpamRuleRejectsWholeBatch()
    {
        var batch = SpamBatch();
        var response = Response(
            batch,
            Decision(
                "m1",
                "ban",
                null,
                "G",
                "Wiederholter Spam.",
                ["spam"],
                0.99,
                "G_SPAM"));

        AssertInvalid(response, batch, "action_rule_invalid");
    }

    [Fact]
    public void Validate_ReplyConfidenceRequiresSelectedTemplateAndValidRange()
    {
        var batch = Batch(1, includeBackseat: false) with
        {
            ReplyTemplates =
            [
                new ChatReplyTemplatePrompt("r1", "event"),
            ],
        };

        AssertInvalid(
            Response(
                batch,
                Decision(
                    "m1",
                    "none",
                    null,
                    null,
                    "",
                    [],
                    0.1,
                    replyConfidence: 0.8)),
            batch,
            "reply_confidence_without_template");
        AssertInvalid(
            Response(
                batch,
                Decision(
                    "m1",
                    "none",
                    null,
                    null,
                    "",
                    [],
                    0.1,
                    replyTemplateId: "r1",
                    replyConfidence: 1.1)),
            batch,
            "reply_confidence_invalid");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(7_200)]
    [InlineData(1_209_600)]
    [InlineData(0)]
    [InlineData(1_209_601)]
    public void Validate_TimeoutOutsideAiTiersRejectsWholeBatch(int seconds)
    {
        var batch = Batch(1, includeBackseat: false);
        var response = Response(
            batch,
            Decision("m1", "timeout", seconds, "G", "spam", ["bad"], 0.9));

        AssertInvalid(response, batch, "timeout_invalid");
    }

    [Fact]
    public void Validate_ActionAndTimeoutCombinationMustMatch()
    {
        var batch = Batch(1, includeBackseat: false);

        AssertInvalid(
            Response(
                batch,
                Decision("m1", "timeout", null, "G", "spam", ["bad"], 0.9)),
            batch,
            "timeout_invalid");
        AssertInvalid(
            Response(
                batch,
                Decision("m1", "delete", 10, "G", "spam", ["bad"], 0.9)),
            batch,
            "timeout_invalid");
        AssertInvalid(
            Response(
                batch,
                Decision(
                    "m1",
                    "ban",
                    86_400,
                    "G",
                    "Eindeutig extreme Beleidigung.",
                    ["bad"],
                    0.99,
                    "G_INSULT")),
            batch,
            "timeout_invalid");
    }

    [Fact]
    public void Validate_ActionReasonLongerThan120CharactersUsesRuleFallback()
    {
        var batch = Batch(1, includeBackseat: false);
        var response = Response(
            batch,
            Decision(
                "m1",
                "delete",
                null,
                "G",
                new string('a', 121),
                ["bad"],
                0.9));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        Assert.Equal(
            "Konfigurierter allgemeiner Regelgrund.",
            Assert.Single(result.Envelope!.Decisions).Reason);
    }

    [Fact]
    public void Validate_UnknownRuleCodeRejectsWholeBatch()
    {
        var batch = Batch(1, includeBackseat: false);
        var response = Response(
            batch,
            Decision(
                "m1",
                "delete",
                null,
                "G",
                "spam",
                ["bad"],
                0.9,
                "G_NOT_ACTIVE"));

        AssertInvalid(response, batch, "rule_inactive");
    }

    [Fact]
    public void Validate_ActionWithoutRuleCodeRejectsWholeBatch()
    {
        var batch = Batch(1, includeBackseat: false);
        var decision = Decision(
            "m1",
            "timeout",
            600,
            "G",
            "Testgrund.",
            ["bad"],
            0.9);
        decision["ruleCode"] = null;

        AssertInvalid(
            Response(batch, decision),
            batch,
            "rule_invalid");
    }

    [Fact]
    public void ValidateWithMissingRuleFallback_UsesMatchingLocalRule()
    {
        var batch = SexualizationSeedBatch();
        var decision = Decision(
            "m1",
            "timeout",
            600,
            "G",
            "Sexualisierende Aussage über eine Person.",
            ["sie sieht sexy aus"],
            0.9);
        decision["ruleCode"] = null;
        var rawResponse = Response(batch, decision);
        var validator = new ModerationEnvelopeValidator();

        Assert.False(validator.Validate(rawResponse, batch).IsValid);

        var result = validator.ValidateWithMissingRuleFallback(
            rawResponse,
            batch);

        Assert.True(result.IsValid);
        Assert.Equal(
            "G_SEXUALIZATION",
            Assert.Single(result.Envelope!.Decisions).RuleCode);
    }

    [Fact]
    public void ValidateWithMissingRuleFallback_UsesGeneralBaseRuleWithoutLocalHit()
    {
        var batch = Batch(1, includeBackseat: false);
        var decision = Decision(
            "m1",
            "delete",
            null,
            "G",
            "Allgemeiner Regelverstoß.",
            ["bad"],
            0.9);
        decision["ruleCode"] = "";

        var result = new ModerationEnvelopeValidator()
            .ValidateWithMissingRuleFallback(
                Response(batch, decision),
                batch);

        Assert.True(result.IsValid);
        Assert.Equal(
            ModerationRuleCodes.OtherAbuse,
            Assert.Single(result.Envelope!.Decisions).RuleCode);
    }

    [Fact]
    public void Validate_RuleAndPolicySetMustMatch()
    {
        var batch = Batch(1, includeBackseat: true);
        var response = Response(
            batch,
            Decision(
                "m1",
                "delete",
                null,
                "B",
                "spoiler",
                ["bad"],
                0.9,
                ModerationRuleCodes.GeneralPhrase));

        AssertInvalid(response, batch, "rule_policy_mismatch");
    }

    [Fact]
    public void Validate_NoneRequiresNullRuleAndExactlyEmptyReason()
    {
        var batch = Batch(1, includeBackseat: false);
        AssertInvalid(
            Response(
                batch,
                Decision(
                    "m1",
                    "none",
                    null,
                    null,
                    "",
                    [],
                    0.1,
                    ModerationRuleCodes.GeneralPhrase)),
            batch,
            "rule_for_none");

        AssertInvalid(
            Response(
                batch,
                Decision("m1", "none", null, null, " ", [], 0.1)),
            batch,
            "reason_for_none");
    }

    [Fact]
    public void Validate_EnglishReasonUsesRuleSpecificGermanFallback()
    {
        var batch = Batch(1, includeBackseat: false);
        var response = Response(
            batch,
            Decision(
                "m1",
                "delete",
                null,
                "G",
                "This content should be removed under policy G.",
                ["bad"],
                0.9,
                "G_INSULT"));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        Assert.Equal(
            "Direkte Beleidigung eines Nutzers oder Streamers.",
            Assert.Single(result.Envelope!.Decisions).Reason);
    }

    [Fact]
    public void Validate_SexualizationSeedProducesCategorizedDefaultTimeout()
    {
        var batch = SexualizationSeedBatch();
        var response = Response(
            batch,
            Decision(
                "m1",
                "timeout",
                600,
                "G",
                "This sexual content should be removed under policy G.",
                ["sie sieht sexy aus"],
                0.94,
                "G_SEXUALIZATION"));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal(ModerationActionKind.Timeout, decision.Action);
        Assert.Equal(600, decision.TimeoutSeconds);
        Assert.Equal("G_SEXUALIZATION", decision.RuleCode);
        Assert.Equal(
            "Sexualisierende Aussage über eine Person.",
            decision.Reason);
    }

    [Fact]
    public void Validate_SpamDecisionPreservesSpamCategoryAndUsesGermanFallback()
    {
        const string spamRuleCode = "G_SPAM";
        var batch = SpamBatch();
        var response = Response(
            batch,
            Decision(
                "m1",
                "timeout",
                600,
                "G",
                "Repeated spam should be removed under policy G.",
                ["spam"],
                0.96,
                spamRuleCode));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal(PolicySetCode.General, decision.PolicySet);
        Assert.Equal(spamRuleCode, decision.RuleCode);
        Assert.Equal("Wiederholter Spam.", decision.Reason);
        var activeRule = Assert.Single(
            batch.Context.EffectivePolicy.ActiveRules,
            rule => rule.RuleCode == spamRuleCode);
        Assert.Equal("Spam", activeRule.Title);
    }

    [Fact]
    public void Validate_DeleteForRepeatedSpamBecomesLightTimeout()
    {
        var batch = SpamBatch();
        var response = Response(
            batch,
            Decision(
                "m1",
                "delete",
                null,
                "G",
                "Wiederholter Spam.",
                ["spam"],
                0.96,
                ModerationRuleCodes.Spam));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal(ModerationActionKind.Timeout, decision.Action);
        Assert.Equal(120, decision.TimeoutSeconds);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(10, false)]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(21, true)]
    public void Validate_EnforcesTwentyEmoteBoundaryForEmoteOnlySpam(
        int emoteCount,
        bool expectedValid)
    {
        var messageText = string.Join(
            ' ',
            Enumerable.Repeat("kappa", emoteCount));
        var batch = SpamBatch(
            messageText,
            emoteCount,
            isTwitchEmoteOnly: true);
        var response = Response(
            batch,
            Decision(
                "m1",
                "timeout",
                120,
                "G",
                "Wiederholter Spam.",
                ["kappa"],
                0.96,
                ModerationRuleCodes.Spam));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.Equal(expectedValid, result.IsValid);
        if (!expectedValid)
        {
            Assert.Contains(
                result.Errors,
                error => error.Code == "spam_emote_threshold_not_met");
        }
    }

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(21, true)]
    public void ValidateV7_EnforcesTwentyEmoteBoundaryForEmoteOnlySpam(
        int emoteCount,
        bool expectedValid)
    {
        var messageText = string.Join(
            ' ',
            Enumerable.Repeat("kappa", emoteCount));
        var batch = SpamBatch(
            messageText,
            emoteCount,
            isTwitchEmoteOnly: true);
        var response = V7Response(
            batch,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["m1"] = V7Moderation(
                    "timeout",
                    120,
                    ModerationRuleCodes.Spam,
                    "Der Chat wurde durch eine große Emotemenge überflutet.",
                    ["kappa"],
                    0.96),
            });

        var result = new ModerationEnvelopeValidator().ValidateV6(response, batch);

        Assert.Equal(expectedValid, result.IsValid);
        if (!expectedValid)
        {
            Assert.Contains(
                result.Issues,
                issue => issue.Code == "spam_emote_threshold_not_met"
                    && issue.MessageId == "m1");
        }
    }

    [Fact]
    public void Validate_OneThroughNineteenNativeEmotesCannotBeRelabeledAsDisruption()
    {
        var messageText = string.Join(
            ' ',
            Enumerable.Repeat("kappa", 19));
        var baseBatch = GeneralRuleBatch(
            ModerationRuleCodes.Disruption,
            "Chatstörung",
            "Gezielte Störung des Chats.",
            messageText);
        var batch = baseBatch with
        {
            Messages = baseBatch.Messages
                .Select(message => message with
                {
                    Message = message.Message with
                    {
                        TwitchEmoteCount = 19,
                        IsTwitchEmoteOnly = true,
                    },
                })
                .ToArray(),
        };
        var legacy = Response(
            batch,
            Decision(
                "m1",
                "timeout",
                600,
                "G",
                "Gezielte Störung des Chats.",
                ["kappa"],
                0.9,
                ModerationRuleCodes.Disruption));
        var sparse = V6Response(
            batch,
            [],
            [
                V6Moderation(
                    "m1",
                    "timeout",
                    600,
                    ModerationRuleCodes.Disruption,
                    "Gezielte Störung des Chats.",
                    ["kappa"],
                    0.9),
            ]);

        Assert.Contains(
            new ModerationEnvelopeValidator().Validate(legacy, batch).Errors,
            error => error.Code == "emote_only_below_spam_threshold");
        Assert.Contains(
            new ModerationEnvelopeValidator().ValidateV6(sparse, batch).Issues,
            issue => issue.Code == "emote_only_below_spam_threshold"
                && issue.MessageId == "m1");
    }

    [Theory]
    [InlineData("delete", null)]
    [InlineData("timeout", 120)]
    [InlineData("timeout", 300)]
    public void Validate_AdvertisingActionAlwaysBecomesBan(
        string action,
        int? timeoutSeconds)
    {
        var batch = GeneralRuleBatch(
            ModerationRuleCodes.Advertising,
            "Werbung",
            "Unaufgeforderte Werbung oder Eigenwerbung.",
            "besucht meinen kanal");
        var response = Response(
            batch,
            Decision(
                "m1",
                action,
                timeoutSeconds,
                "G",
                "Unaufgeforderte Werbung oder Eigenwerbung.",
                ["meinen kanal"],
                0.97,
                ModerationRuleCodes.Advertising));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal(ModerationActionKind.Ban, decision.Action);
        Assert.Null(decision.TimeoutSeconds);
    }

    [Theory]
    [InlineData("delete", null)]
    [InlineData("timeout", 120)]
    [InlineData("ban", null)]
    public void Validate_BeggingActionAlwaysBecomesDayTimeout(
        string action,
        int? timeoutSeconds)
    {
        var batch = GeneralRuleBatch(
            ModerationRuleCodes.Begging,
            "Gebettel",
            "Betteln nach Subs, Geschenken oder Giveaways.",
            "schenk mir bitte einen sub");
        var response = Response(
            batch,
            Decision(
                "m1",
                action,
                timeoutSeconds,
                "G",
                "Betteln nach Subs, Geschenken oder Giveaways.",
                ["schenk mir bitte einen sub"],
                0.95,
                ModerationRuleCodes.Begging));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal(ModerationActionKind.Timeout, decision.Action);
        Assert.Equal(86_400, decision.TimeoutSeconds);
    }

    [Fact]
    public void Validate_NonStringActionReasonUsesConfiguredFallback()
    {
        var batch = Batch(1, includeBackseat: false);
        var decision = Decision(
            "m1",
            "delete",
            null,
            "G",
            "placeholder",
            ["bad"],
            0.9);
        decision["reason"] = 42;

        var result = new ModerationEnvelopeValidator().Validate(
            Response(batch, decision),
            batch);

        Assert.True(result.IsValid);
        Assert.Equal(
            "Konfigurierter allgemeiner Regelgrund.",
            Assert.Single(result.Envelope!.Decisions).Reason);
    }

    [Fact]
    public void Validate_PersonalDataCannotProduceLearningTriggers()
    {
        var batch = Batch(1, includeBackseat: false);
        var response = Response(
            batch,
            Decision(
                "m1",
                "delete",
                null,
                "G",
                "Persönliche Daten veröffentlicht.",
                ["bad"],
                0.9,
                ModerationRuleCodes.PersonalData));

        AssertInvalid(response, batch, "triggers_learning_forbidden");
    }

    [Theory]
    [MemberData(nameof(InvalidTriggers))]
    public void Validate_TriggerMustBeUniqueLowercaseSubstring(string[] triggers)
    {
        var batch = Batch(1, includeBackseat: false);
        var response = Response(
            batch,
            Decision("m1", "delete", null, "G", "spam", triggers, 0.9));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.False(result.IsValid);
        Assert.Null(result.Envelope);
        Assert.Contains(
            result.Errors,
            error => error.Code is "trigger_invalid" or "triggers_too_many");
    }

    public static TheoryData<string[]> InvalidTriggers =>
        new()
        {
            { ["BAD"] },
            { ["not present"] },
            { ["bad", "bad"] },
            { ["bad", "text", "and", "spoiler"] },
        };

    [Fact]
    public void Validate_BatchIdAndVersionMustMatchExactly()
    {
        var batch = Batch(1, includeBackseat: false);
        var decision = Decision("m1", "none", null, null, "", [], 0.1);
        var wrongBatch = Response(batch, [decision], Guid.NewGuid().ToString());
        var wrongVersion = Response(
            batch,
            [decision],
            batch.BatchId.ToString(),
            "twitchai.moderation.response.v1");

        AssertInvalid(wrongBatch, batch, "batch_id_invalid");
        AssertInvalid(wrongVersion, batch, "version_invalid");
    }

    [Fact]
    public void Validate_MalformedJsonNeverProducesPartialEnvelope()
    {
        var batch = Batch(1, includeBackseat: false);

        var result = new ModerationEnvelopeValidator().Validate("{", batch);

        Assert.False(result.IsValid);
        Assert.Null(result.Envelope);
        Assert.Equal("json_invalid", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void ValidateDetailed_V6SparseResponseDerivesPolicyAndDecoratesNoActionReply()
    {
        var batch = Batch(
            2,
            includeBackseat: false,
            firstMessage: "du bist ein idiot") with
        {
            ReplyTemplates = [new("r1", "wie lange geht das event noch")],
        };
        var response = V6Response(
            batch,
            noActionMessageIds: [],
            moderations:
            [
                V6Moderation(
                    "m1",
                    "timeout",
                    600,
                    "G_INSULT",
                    "Beleidigung eines Nutzers oder Streamers.",
                    ["du bist ein idiot"],
                    0.94),
            ],
            replies:
            [
                new Dictionary<string, object?>
                {
                    ["messageId"] = "m2",
                    ["replyTemplateId"] = "r1",
                    ["confidence"] = 0.88,
                },
            ]);

        var result = new ModerationEnvelopeValidator()
            .ValidateDetailed(response, batch);

        Assert.True(result.IsValid);
        Assert.False(result.RequiresFullRepair);
        Assert.Empty(result.RepairMessageIds);
        Assert.Equal(2, result.Envelope!.Decisions.Count);
        var moderation = result.Envelope.Decisions[0];
        Assert.Equal(PolicySetCode.General, moderation.PolicySet);
        Assert.Equal("G_INSULT", moderation.RuleCode);
        var noAction = result.Envelope.Decisions[1];
        Assert.Equal(ModerationActionKind.None, noAction.Action);
        Assert.Equal(1D, noAction.Confidence);
        Assert.Equal("r1", noAction.ReplyTemplateId);
        Assert.Equal(0.88, noAction.ReplyConfidence);
    }

    [Fact]
    public void ValidateDetailed_V6MissingMessageKeepsValidDecisionForTargetedRepair()
    {
        var batch = Batch(2, includeBackseat: false);
        var response = V6Response(
            batch,
            noActionMessageIds: ["m1"],
            moderations: []);

        var result = new ModerationEnvelopeValidator()
            .ValidateDetailed(response, batch);

        Assert.False(result.IsValid);
        Assert.False(result.RequiresFullRepair);
        Assert.Equal(["m2"], result.RepairMessageIds);
        var valid = Assert.Single(result.ValidDecisions);
        Assert.Equal("m1", valid.TemporaryMessageId);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("decision_missing", issue.Code);
        Assert.Equal("m2", issue.MessageId);
    }

    [Fact]
    public void ValidateDetailed_V6InvalidSingleModerationKeepsOtherMessages()
    {
        var batch = Batch(2, includeBackseat: false);
        var response = V6Response(
            batch,
            noActionMessageIds: ["m1"],
            moderations:
            [
                V6Moderation(
                    "m2",
                    "timeout",
                    600,
                    "UNKNOWN",
                    "Verstoß.",
                    ["ordinary"],
                    0.9),
            ]);

        var result = new ModerationEnvelopeValidator()
            .ValidateDetailed(response, batch);

        Assert.False(result.IsValid);
        Assert.False(result.RequiresFullRepair);
        Assert.Equal(["m2"], result.RepairMessageIds);
        Assert.Equal("m1", Assert.Single(result.ValidDecisions).TemporaryMessageId);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "rule_inactive" && issue.MessageId == "m2");
    }

    [Fact]
    public void ValidateDetailed_V6MissingRuleUsesUnambiguousLocalMatchBeforeRepair()
    {
        var batch = SexualizationSeedBatch();
        var moderation = V6Moderation(
            "m1",
            "timeout",
            600,
            ruleCode: null,
            "Sexualisierende Aussage über eine Person.",
            ["sie sieht sexy aus"],
            0.9);
        moderation.Remove("ruleCode");
        var response = V6Response(
            batch,
            noActionMessageIds: [],
            moderations: [moderation]);

        var result = new ModerationEnvelopeValidator()
            .ValidateDetailed(response, batch);

        Assert.True(result.IsValid);
        Assert.Equal(
            "G_SEXUALIZATION",
            Assert.Single(result.Envelope!.Decisions).RuleCode);
        Assert.Equal(["m1"], result.LocalFallbackMessageIds);
    }

    [Fact]
    public void ValidateDetailed_V6OverlappingGroupsRepairOnlyOverlappingMessage()
    {
        var batch = Batch(2, includeBackseat: false);
        var response = V6Response(
            batch,
            noActionMessageIds: ["m1", "m2"],
            moderations:
            [
                V6Moderation(
                    "m2",
                    "timeout",
                    600,
                    ModerationRuleCodes.GeneralPhrase,
                    "Sonstiger Verstoß gegen die Chatregeln.",
                    ["ordinary"],
                    0.8),
            ]);

        var result = new ModerationEnvelopeValidator()
            .ValidateDetailed(response, batch);

        Assert.False(result.IsValid);
        Assert.False(result.RequiresFullRepair);
        Assert.Equal(["m2"], result.RepairMessageIds);
        Assert.Equal("m1", Assert.Single(result.ValidDecisions).TemporaryMessageId);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "message_id_duplicate" && issue.MessageId == "m2");
    }

    [Fact]
    public void ValidateV6_DeleteRequiresTimeoutPropertyToBeAbsent()
    {
        var batch = Batch(1, includeBackseat: false);
        var moderation = V6Moderation(
            "m1",
            "delete",
            null,
            ModerationRuleCodes.GeneralPhrase,
            "Unnötige Nachricht.",
            ["bad"],
            0.8);
        var valid = V6Response(batch, [], [moderation]);

        Assert.True(new ModerationEnvelopeValidator().ValidateV6(valid, batch).IsValid);

        moderation["timeoutSeconds"] = null;
        var invalid = new ModerationEnvelopeValidator().ValidateV6(
            V6Response(batch, [], [moderation]),
            batch);
        Assert.False(invalid.IsValid);
        Assert.Contains(
            invalid.Issues,
            issue => issue.Code == "timeout_for_non_timeout"
                && issue.MessageId == "m1");
    }

    [Fact]
    public void ValidateV6_ReplyMustBeDisjointFromNoActionGroup()
    {
        var batch = Batch(1, includeBackseat: false) with
        {
            ReplyTemplates = [new("r1", "event dauer")],
        };
        var response = V6Response(
            batch,
            ["m1"],
            [],
            [
                new Dictionary<string, object?>
                {
                    ["messageId"] = "m1",
                    ["replyTemplateId"] = "r1",
                    ["confidence"] = 0.9,
                },
            ]);

        var result = new ModerationEnvelopeValidator().ValidateV6(response, batch);

        Assert.False(result.IsValid);
        Assert.Equal(["m1"], result.RepairMessageIds);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "reply_message_id_duplicate");
    }

    [Fact]
    public void Validate_LegacyReasonContainingV6VersionDoesNotChangeDispatcher()
    {
        var batch = Batch(1, includeBackseat: false);
        var response = Response(
            batch,
            Decision(
                "m1",
                "timeout",
                600,
                "G",
                ModerationEnvelopeValidator.SparseResponseEnvelopeVersion,
                ["bad"],
                0.9));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateV7_KeyedTwentyMessageResponseIsCompleteWithoutRepair()
    {
        var batch = Batch(20, includeBackseat: false);
        var decisions = batch.Messages.ToDictionary(
            message => message.TemporaryMessageId,
            _ => (object?)null,
            StringComparer.Ordinal);

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(batch, decisions),
            batch);

        Assert.True(result.IsValid);
        Assert.False(result.RequiresFullRepair);
        Assert.Empty(result.RepairMessageIds);
        Assert.Equal(20, result.Envelope!.Decisions.Count);
        Assert.All(
            result.Envelope.Decisions,
            decision => Assert.Equal(ModerationActionKind.None, decision.Action));
    }

    [Fact]
    public void ValidateV7_GenericRuleReasonRequiresTargetedAiRepair()
    {
        var batch = Batch(
            1,
            includeBackseat: false,
            firstMessage: "du bist ein idiot");
        var personalAttackRule = new ModerationRuleDefinition(
            ModerationRuleCodes.PersonalAttack,
            PolicySetCode.General,
            "Beleidigung und Belästigung",
            ModerationRuleCodes.PersonalAttackPromptInstruction,
            ModerationActionKind.Timeout,
            600,
            "Beleidigung, Belästigung oder unangebrachte persönliche Annäherung an einen Nutzer oder Streamer.",
            Priority: 50);
        batch = batch with
        {
            Context = batch.Context with
            {
                EffectivePolicy = batch.Context.EffectivePolicy with
                {
                    ActiveRules =
                    [
                        .. batch.Context.EffectivePolicy.ActiveRules,
                        personalAttackRule,
                    ],
                },
            },
        };
        var moderation = V7Moderation(
            "timeout",
            600,
            ModerationRuleCodes.PersonalAttack,
            "Beleidigung, Belästigung oder unangebrachte persönliche Annäherung an einen Nutzer oder Streamer.",
            ["du bist ein idiot"],
            0.95);
        moderation["evidenceKind"] = "direct_insult";

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(
                batch,
                new Dictionary<string, object?> { ["m1"] = moderation }),
            batch);

        Assert.False(result.IsValid);
        Assert.Equal(["m1"], result.RepairMessageIds);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "violation_reason_invalid"
                && issue.MessageId == "m1");
    }

    [Fact]
    public void ValidateV7_DirectSecondPersonReasonRequiresTargetedAiRepair()
    {
        var batch = PersonalAttackBatch("du bist ein idiot");
        var moderation = V7Moderation(
            "timeout",
            600,
            ModerationRuleCodes.PersonalAttack,
            "Du hast @mayvo persönlich beleidigt.",
            ["du bist ein idiot"],
            0.95);

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(
                batch,
                new Dictionary<string, object?> { ["m1"] = moderation }),
            batch);

        Assert.False(result.IsValid);
        Assert.Equal(["m1"], result.RepairMessageIds);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "violation_reason_invalid"
                && issue.MessageId == "m1");
    }

    [Theory]
    [InlineData("timeout", 600)]
    [InlineData("ban", null)]
    public void ValidateV7_ComposesStructuredReasonForTimeoutAndBan(
        string action,
        int? timeoutSeconds)
    {
        var batch = PersonalAttackBatch("@mayvo du bist völlig unfähig");
        var moderation = V7Moderation(
            action,
            timeoutSeconds,
            ModerationRuleCodes.PersonalAttack,
            "Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers.",
            ["du bist völlig unfähig"],
            0.95);

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(
                batch,
                new Dictionary<string, object?> { ["m1"] = moderation }),
            batch);

        Assert.True(result.IsValid);
        Assert.Equal(
            "Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers. Chatregeln künftig bitte beachten.",
            Assert.Single(result.Envelope!.Decisions).Reason);
    }

    [Fact]
    public void ValidateV7_InvalidGuidanceRequiresTargetedAiRepair()
    {
        var batch = PersonalAttackBatch("@mayvo du bist völlig unfähig");
        var moderation = V7Moderation(
            "timeout",
            600,
            ModerationRuleCodes.PersonalAttack,
            "Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers.",
            ["du bist völlig unfähig"],
            0.95);
        moderation["guidance"] = "Du solltest Beleidigungen künftig vermeiden.";

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(
                batch,
                new Dictionary<string, object?> { ["m1"] = moderation }),
            batch);

        Assert.False(result.IsValid);
        Assert.Equal(["m1"], result.RepairMessageIds);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "guidance_invalid"
                && issue.MessageId == "m1");
    }

    [Fact]
    public void ValidateV7_UnquotedTriggerPhraseInReasonRequiresTargetedAiRepair()
    {
        var batch = PersonalAttackBatch(
            "@mayvo persönliche vergeltung erwartet dich");
        var moderation = V7Moderation(
            "timeout",
            600,
            ModerationRuleCodes.PersonalAttack,
            "Persönliche Vergeltung gegen @mayvo wurde angekündigt.",
            ["@mayvo persönliche vergeltung"],
            0.95);

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(
                batch,
                new Dictionary<string, object?> { ["m1"] = moderation }),
            batch);

        Assert.False(result.IsValid);
        Assert.Equal(["m1"], result.RepairMessageIds);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "reason_contains_trigger_phrase"
                && issue.MessageId == "m1");
    }

    [Fact]
    public void ValidateV7_InvalidSingleDecisionKeepsOtherMessagesForTargetedRepair()
    {
        var batch = Batch(2, includeBackseat: false);
        var decisions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["m1"] = null,
            ["m2"] = V7Moderation(
                "timeout",
                600,
                "UNKNOWN",
                "Verstoß.",
                ["ordinary"],
                0.9),
        };

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(batch, decisions),
            batch);

        Assert.False(result.IsValid);
        Assert.False(result.RequiresFullRepair);
        Assert.Equal(["m2"], result.RepairMessageIds);
        Assert.Equal("m1", Assert.Single(result.ValidDecisions).TemporaryMessageId);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "rule_inactive" && issue.MessageId == "m2");
    }

    [Fact]
    public void ValidateV7_MissingKeyRequiresOnlyThatMessageToBeRepaired()
    {
        var batch = Batch(2, includeBackseat: false);
        var decisions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["m1"] = null,
        };

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(batch, decisions),
            batch);

        Assert.False(result.IsValid);
        Assert.False(result.RequiresFullRepair);
        Assert.Equal(["m2"], result.RepairMessageIds);
        Assert.Equal("decision_missing", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void ValidateV7_PersonalDataTriggersAreClearedLocallyWithoutRepair()
    {
        var batch = Batch(
            1,
            includeBackseat: false,
            firstMessage: "meine adresse ist musterstraße 1");
        var decisions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["m1"] = V7Moderation(
                "timeout",
                600,
                ModerationRuleCodes.PersonalData,
                "Private Adressdaten wurden veröffentlicht.",
                ["musterstraße 1"],
                0.95),
        };

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(batch, decisions),
            batch);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Empty(decision.TriggerPhrases);
        Assert.Equal(["m1"], result.LocalFallbackMessageIds);
        Assert.Empty(result.RepairMessageIds);
    }

    [Fact]
    public void ValidateV7_InvalidOptionalGeneralTriggersAreSanitizedLocally()
    {
        var batch = Batch(
            1,
            includeBackseat: false,
            firstMessage: "du bist idiot");
        var decisions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["m1"] = V7Moderation(
                "timeout",
                600,
                ModerationRuleCodes.Insult,
                "Nutzer oder Streamer wurde direkt persönlich beleidigt.",
                ["IDIOT", "nicht im text", "du bist idiot", "idiot"],
                0.9),
        };

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(batch, decisions),
            batch);

        Assert.True(result.IsValid);
        Assert.Equal(
            ["du bist idiot"],
            Assert.Single(result.Envelope!.Decisions).TriggerPhrases);
        Assert.Equal(["m1"], result.LocalFallbackMessageIds);
        Assert.Empty(result.RepairMessageIds);
    }

    [Fact]
    public void ValidateV7_ContextFreeSingleWordTriggerIsDroppedWithoutRepair()
    {
        var batch = Batch(
            1,
            includeBackseat: false,
            firstMessage: "du bist idiot");
        var decisions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["m1"] = V7Moderation(
                "timeout",
                600,
                ModerationRuleCodes.Insult,
                "Nutzer oder Streamer wurde direkt persönlich beleidigt.",
                ["idiot"],
                0.9),
        };

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(batch, decisions),
            batch);

        Assert.True(result.IsValid);
        Assert.Empty(Assert.Single(result.Envelope!.Decisions).TriggerPhrases);
        Assert.Equal(["m1"], result.LocalFallbackMessageIds);
        Assert.Empty(result.RepairMessageIds);
    }

    [Fact]
    public void ValidateV7_InsultFiltersIrrelevantEvidenceBeforeKeepingCompleteClause()
    {
        var batch = Batch(
            1,
            includeBackseat: false,
            firstMessage: "@shlorox dann halt sie fest kappa du bist ein idiot");
        var decisions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["m1"] = V7Moderation(
                "timeout",
                600,
                ModerationRuleCodes.Insult,
                "Nutzer oder Streamer wurde direkt persönlich beleidigt.",
                ["dann halt", "sie fest", "@shlorox", "du bist ein idiot"],
                0.9),
        };

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(batch, decisions),
            batch);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal(ModerationActionKind.Timeout, decision.Action);
        Assert.Equal(ModerationRuleCodes.Insult, decision.RuleCode);
        Assert.Equal(["du bist ein idiot"], decision.TriggerPhrases);
        Assert.Equal(["m1"], result.LocalFallbackMessageIds);
        Assert.Empty(result.RepairMessageIds);
    }

    [Fact]
    public void ValidateV7_InsultWithoutValidEvidenceFallsBackToNoActionLocally()
    {
        var batch = Batch(
            1,
            includeBackseat: false,
            firstMessage: "@dennisith dann halt sie fest kappa");
        var decisions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["m1"] = V7Moderation(
                "timeout",
                600,
                ModerationRuleCodes.Insult,
                "Nutzer oder Streamer wurde direkt persönlich beleidigt.",
                ["dann halt", "sie fest"],
                0.9),
        };

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(batch, decisions),
            batch);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal(ModerationActionKind.None, decision.Action);
        Assert.Null(decision.TimeoutSeconds);
        Assert.Null(decision.PolicySet);
        Assert.Null(decision.RuleCode);
        Assert.Empty(decision.Reason);
        Assert.Empty(decision.TriggerPhrases);
        Assert.Equal(["m1"], result.LocalFallbackMessageIds);
        Assert.Empty(result.RepairMessageIds);
    }

    [Fact]
    public void ValidateV7_BackseatTriggerEvidenceRemainsStrict()
    {
        var batch = Batch(
            1,
            includeBackseat: true,
            firstMessage: "geh nach links");
        var decisions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["m1"] = V7Moderation(
                "timeout",
                600,
                ModerationRuleCodes.Backseatgaming,
                "Der Streamer wurde ungefragt zu einer Spielhandlung angewiesen.",
                ["links"],
                0.9),
        };

        var result = new ModerationEnvelopeValidator().ValidateV6(
            V7Response(batch, decisions),
            batch);

        Assert.False(result.IsValid);
        Assert.Equal(["m1"], result.RepairMessageIds);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "backseat_evidence_insufficient");
    }

    private static void AssertInvalid(
        string response,
        ModerationBatch batch,
        string expectedCode)
    {
        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.False(result.IsValid);
        Assert.Null(result.Envelope);
        Assert.Contains(result.Errors, error => error.Code == expectedCode);
    }

    private static string V6Response(
        ModerationBatch batch,
        IReadOnlyList<string> noActionMessageIds,
        IReadOnlyList<Dictionary<string, object?>> moderations,
        IReadOnlyList<Dictionary<string, object?>>? replies = null)
    {
        var root = new Dictionary<string, object?>
        {
            ["envelopeVersion"] = ModerationEnvelopeValidator.SparseResponseEnvelopeVersion,
            ["batchId"] = batch.BatchId,
            ["noActionMessageIds"] = noActionMessageIds,
            ["moderations"] = moderations,
        };
        if (replies is not null)
        {
            root["replies"] = replies;
        }

        return JsonSerializer.Serialize(root);
    }

    private static string V7Response(
        ModerationBatch batch,
        IReadOnlyDictionary<string, object?> decisions) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["envelopeVersion"] =
                    ModerationEnvelopeValidator.KeyedResponseEnvelopeVersion,
                ["batchId"] = batch.BatchId,
                ["decisions"] = decisions,
            });

    private static Dictionary<string, object?> V7Moderation(
        string action,
        int? timeoutSeconds,
        string ruleCode,
        string reason,
        string[] triggerPhrases,
        double confidence)
    {
        var evidenceKinds = ModerationEvidenceKinds.ForRule(ruleCode);
        var result = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["ruleCode"] = ruleCode,
            ["evidenceKind"] = evidenceKinds.Count > 0
                ? evidenceKinds[0]
                : null,
            ["sensitiveDataType"] = string.Equals(
                ruleCode,
                ModerationRuleCodes.PersonalData,
                StringComparison.Ordinal)
                    ? "address"
                    : null,
            ["violationReason"] = action is "timeout" or "ban"
                ? reason
                : string.Empty,
            ["guidance"] = action is "timeout" or "ban"
                ? "Chatregeln künftig bitte beachten."
                : string.Empty,
            ["triggerPhrases"] = triggerPhrases,
            ["confidence"] = confidence,
        };
        if (timeoutSeconds is not null)
        {
            result["timeoutSeconds"] = timeoutSeconds;
        }

        return result;
    }

    private static Dictionary<string, object?> V11Moderation(
        string action,
        int? timeoutSeconds,
        string ruleCode,
        string evidenceKind,
        string reason,
        string[] triggerPhrases,
        double confidence)
    {
        var result = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["ruleCode"] = ruleCode,
            ["evidenceKind"] = evidenceKind,
            ["reason"] = reason,
            ["triggerPhrases"] = triggerPhrases,
            ["confidence"] = confidence,
        };
        if (timeoutSeconds is not null)
        {
            result["timeoutSeconds"] = timeoutSeconds;
        }

        return result;
    }

    private static Dictionary<string, object?> V6Moderation(
        string messageId,
        string action,
        int? timeoutSeconds,
        string? ruleCode,
        string reason,
        string[] triggerPhrases,
        double confidence)
    {
        var result = new Dictionary<string, object?>
        {
            ["messageId"] = messageId,
            ["action"] = action,
            ["ruleCode"] = ruleCode,
            ["reason"] = reason,
            ["triggerPhrases"] = triggerPhrases,
            ["confidence"] = confidence,
        };
        if (timeoutSeconds is not null)
        {
            result["timeoutSeconds"] = timeoutSeconds;
        }

        return result;
    }

    private static string Response(
        ModerationBatch batch,
        params Dictionary<string, object?>[] decisions)
    {
        return Response(
            batch,
            decisions,
            batch.BatchId.ToString(),
            ModerationEnvelopeValidator.ResponseEnvelopeVersion);
    }

    private static string Response(
        ModerationBatch batch,
        IReadOnlyList<Dictionary<string, object?>> decisions,
        string batchId,
        string envelopeVersion = ModerationEnvelopeValidator.ResponseEnvelopeVersion)
    {
        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["envelopeVersion"] = envelopeVersion,
                ["batchId"] = batchId,
                ["decisions"] = decisions,
            });
    }

    private static Dictionary<string, object?> Decision(
        string messageId,
        string action,
        int? timeoutSeconds,
        string? policySet,
        string reason,
        string[] triggerPhrases,
        double confidence,
        string? ruleCode = null,
        string? replyTemplateId = null,
        double? replyConfidence = null)
    {
        var resolvedRuleCode = ruleCode ?? policySet switch
        {
            "G" => ModerationRuleCodes.GeneralPhrase,
            "B" => ModerationRuleCodes.Backseatgaming,
            _ => null,
        };
        return new Dictionary<string, object?>
        {
            ["messageId"] = messageId,
            ["action"] = action,
            ["timeoutSeconds"] = timeoutSeconds,
            ["policySet"] = policySet,
            ["ruleCode"] = resolvedRuleCode,
            ["evidenceKind"] = null,
            ["sensitiveDataType"] = null,
            ["reason"] = reason,
            ["triggerPhrases"] = triggerPhrases,
            ["confidence"] = confidence,
            ["replyTemplateId"] = replyTemplateId,
            ["replyConfidence"] = replyConfidence,
        };
    }

    private static ModerationBatch Batch(
        int messageCount,
        bool includeBackseat,
        string? firstMessage = null)
    {
        var now = new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);
        var channel = new ChannelContextSnapshot(
            "channel-1",
            includeBackseat ? "32982" : "509658",
            includeBackseat ? "Grand Theft Auto V" : "Just Chatting",
            now,
            Revision: 1,
            IsVerified: true);
        var revisions = new List<PolicySetRevision>
        {
            new(PolicySetCode.General, 1),
        };
        if (includeBackseat)
        {
            revisions.Add(new PolicySetRevision(PolicySetCode.Backseatgaming, 1));
        }

        var policy = new EffectivePolicySnapshot(revisions, "policy")
        {
            ActiveRules =
            [
                Rule(ModerationRuleCodes.GeneralPhrase, PolicySetCode.General, 100),
                Rule("G_INSULT", PolicySetCode.General, 70),
                Rule(ModerationRuleCodes.PersonalData, PolicySetCode.General, 10),
                .. (includeBackseat
                    ? new[]
                    {
                        Rule(
                            ModerationRuleCodes.Backseatgaming,
                            PolicySetCode.Backseatgaming,
                            100),
                    }
                    : []),
            ],
        };
        var messages = Enumerable.Range(1, messageCount)
            .Select(index =>
            {
                var original = index == 1
                    ? firstMessage ?? "BAD text and spoiler"
                    : "ordinary chat";
                var identity = ChatMessageIdentityV1.Create(
                    $"event-{index}",
                    $"twitch-{index}",
                    "channel-1",
                    $"user-{index}",
                    $"user-{index}",
                    $"User {index}",
                    original,
                    now.AddMilliseconds(index));
                return new ModerationBatchMessage(
                    $"m{index}",
                    new ChatMessageSnapshot(
                        identity,
                        new ChatTextNormalizer().Process(original),
                        channel,
                        IngressSequence: index),
                    []);
            })
            .ToArray();

        return new ModerationBatch(
            Guid.Parse("dd6f6ee6-91c1-4d37-bfe9-8955bf71401b"),
            new ModerationBatchContext(channel, policy),
            messages,
            now,
            now.AddSeconds(1),
            BatchSealReason.ManualFlush);
    }

    private static ModerationBatch SexualizationSeedBatch()
    {
        var batch = Batch(1, includeBackseat: false);
        var channel = batch.Context.Channel;
        var original = "Sie sieht sexy aus";
        var identity = ChatMessageIdentityV1.Create(
            "event-sexualization",
            "twitch-sexualization",
            channel.ChannelId,
            "user-sexualization",
            "user-sexualization",
            "User Sexualization",
            original,
            batch.OpenedAtUtc);
        var rule = new ModerationRuleDefinition(
            "G_SEXUALIZATION",
            PolicySetCode.General,
            "Sexualisierung",
            "Erkenne sexualisierende Aussagen über Personen.",
            ModerationActionKind.Timeout,
            600,
            "Sexualisierende Aussage über eine Person.",
            Priority: 50);
        var policy = batch.Context.EffectivePolicy with
        {
            ActiveRules =
            [
                .. batch.Context.EffectivePolicy.ActiveRules,
                rule,
            ],
        };
        var message = new ModerationBatchMessage(
            "m1",
            new ChatMessageSnapshot(
                identity,
                new ChatTextNormalizer().Process(original),
                channel,
                IngressSequence: 1),
            [
                new BlacklistMatch(
                    PhraseId: 1,
                    RuleCode: rule.RuleCode,
                    PolicySet: PolicySetCode.General,
                    MatchedPhrase: "sie sieht sexy aus"),
            ]);
        return batch with
        {
            Context = new ModerationBatchContext(channel, policy),
            Messages = [message],
        };
    }

    private static ModerationBatch SpamBatch(
        string firstMessage = "spam spam spam",
        int twitchEmoteCount = 0,
        bool isTwitchEmoteOnly = false)
    {
        const string spamRuleCode = "G_SPAM";
        var batch = Batch(
            1,
            includeBackseat: false,
            firstMessage: firstMessage);
        var channel = batch.Context.Channel;
        var rule = new ModerationRuleDefinition(
            spamRuleCode,
            PolicySetCode.General,
            "Spam",
            "Erkenne wiederholten Spam.",
            ModerationActionKind.Timeout,
            600,
            "Wiederholter Spam.",
            Priority: 80);
        var policy = batch.Context.EffectivePolicy with
        {
            ActiveRules =
            [
                .. batch.Context.EffectivePolicy.ActiveRules,
                rule,
            ],
        };

        return batch with
        {
            Context = new ModerationBatchContext(channel, policy),
            Messages = batch.Messages
                .Select(message => message with
                {
                    Message = message.Message with
                    {
                        TwitchEmoteCount = twitchEmoteCount,
                        IsTwitchEmoteOnly = isTwitchEmoteOnly,
                    },
                })
                .ToArray(),
        };
    }

    private static ModerationBatch GeneralRuleBatch(
        string ruleCode,
        string title,
        string publicReason,
        string original)
    {
        var batch = Batch(
            1,
            includeBackseat: false,
            firstMessage: original);
        var rule = new ModerationRuleDefinition(
            ruleCode,
            PolicySetCode.General,
            title,
            publicReason,
            ModerationActionKind.Timeout,
            600,
            publicReason,
            Priority: 75);
        var policy = batch.Context.EffectivePolicy with
        {
            ActiveRules =
            [
                .. batch.Context.EffectivePolicy.ActiveRules,
                rule,
            ],
        };
        return batch with
        {
            Context = new ModerationBatchContext(
                batch.Context.Channel,
                policy),
        };
    }

    private static ModerationBatch AutoModBatch(
        string text,
        string autoModReason)
    {
        var batch = SpamBatch(text);
        return batch with
        {
            Messages = batch.Messages
                .Select(message => message with
                {
                    Message = message.Message with
                    {
                        IsAutoModHeld = true,
                        AutoModReason = autoModReason,
                    },
                })
                .ToArray(),
        };
    }

    private static ModerationBatch SpamBatch(string[] messages)
    {
        ArgumentOutOfRangeException.ThrowIfZero(messages.Length);
        var template = SpamBatch(messages[0]);
        var channel = template.Context.Channel;
        var batchMessages = messages
            .Select((text, index) =>
            {
                var sequence = index + 1;
                var identity = ChatMessageIdentityV1.Create(
                    $"event-spam-{sequence}",
                    $"twitch-spam-{sequence}",
                    channel.ChannelId,
                    $"user-spam-{sequence}",
                    $"user-spam-{sequence}",
                    $"User Spam {sequence}",
                    text,
                    template.OpenedAtUtc.AddMilliseconds(sequence));
                return new ModerationBatchMessage(
                    $"m{sequence}",
                    new ChatMessageSnapshot(
                        identity,
                        new ChatTextNormalizer().Process(text),
                        channel,
                        sequence),
                    []);
            })
            .ToArray();
        return template with { Messages = batchMessages };
    }

    private static ModerationBatch PersonalAttackBatch(string original) =>
        GeneralRuleBatch(
            ModerationRuleCodes.PersonalAttack,
            "Beleidigung und Belästigung",
            "Beleidigung, Belästigung oder unangebrachte persönliche Annäherung an einen Nutzer oder Streamer.",
            original);

    private static ModerationRuleDefinition Rule(
        string code,
        PolicySetCode policySet,
        int priority) =>
        new(
            code,
            policySet,
            code,
            "test",
            ModerationActionKind.Delete,
            null,
            code switch
            {
                "G_INSULT" =>
                    "Direkte Beleidigung eines Nutzers oder Streamers.",
                ModerationRuleCodes.PersonalData =>
                    "Veröffentlichung persönlicher Daten.",
                ModerationRuleCodes.Backseatgaming =>
                    "Unaufgeforderter Spielhinweis oder Spoiler.",
                _ => "Konfigurierter allgemeiner Regelgrund.",
            },
            priority,
            ModerationRuleCodes.AllowsTriggerLearning(code));
}

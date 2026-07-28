using System.Text.Json;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class ModerationEnvelopeValidatorTests
{
    [Fact]
    public void Validate_ValidOneToOneEnvelopeReturnsTypedDecisions()
    {
        var batch = Batch(
            2,
            includeBackseat: true,
            firstMessage: "fahr links, dort ist eine falle");
        var response = Response(
            batch,
            Decision("m1", "timeout", 300, "B", "backseat", ["fahr links"], 0.91),
            Decision("m2", "none", null, null, "", [], 0.12));

        var result = new ModerationEnvelopeValidator().Validate(response, batch);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Envelope);
        Assert.Equal(2, result.Envelope.Decisions.Count);
        Assert.Equal(ModerationActionKind.Timeout, result.Envelope.Decisions[0].Action);
        Assert.Equal(PolicySetCode.Backseatgaming, result.Envelope.Decisions[0].PolicySet);
        Assert.Equal(ModerationRuleCodes.Backseatgaming, result.Envelope.Decisions[0].RuleCode);
        Assert.Equal(300, result.Envelope.Decisions[0].TimeoutSeconds);
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
        Assert.Equal("G_INSULT", Assert.Single(result.Envelope.Decisions).RuleCode);
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
    [InlineData(0)]
    [InlineData(1_209_601)]
    public void Validate_InvalidTimeoutRangeRejectsWholeBatch(int seconds)
    {
        var batch = Batch(1, includeBackseat: false);
        var response = Response(
            batch,
            Decision("m1", "timeout", seconds, "G", "spam", ["bad"], 0.9));

        AssertInvalid(response, batch, "timeout_invalid");
    }

    [Fact]
    public void Validate_MissingTimeoutForTimeoutAndDurationForDeleteAreInvalid()
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
            "Direkte Beleidigung gegen einen Nutzer.",
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
        return new Dictionary<string, object?>
        {
            ["messageId"] = messageId,
            ["action"] = action,
            ["timeoutSeconds"] = timeoutSeconds,
            ["policySet"] = policySet,
            ["ruleCode"] = ruleCode ?? policySet switch
            {
                "G" => ModerationRuleCodes.GeneralPhrase,
                "B" => ModerationRuleCodes.Backseatgaming,
                _ => null,
            },
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

    private static ModerationBatch SpamBatch()
    {
        const string spamRuleCode = "G_SPAM";
        var batch = Batch(
            1,
            includeBackseat: false,
            firstMessage: "spam spam spam");
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
        };
    }

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
                "G_INSULT" => "Direkte Beleidigung gegen einen Nutzer.",
                ModerationRuleCodes.PersonalData =>
                    "Veröffentlichung persönlicher Daten.",
                ModerationRuleCodes.Backseatgaming =>
                    "Unaufgeforderter Spielhinweis oder Spoiler.",
                _ => "Konfigurierter allgemeiner Regelgrund.",
            },
            priority,
            ModerationRuleCodes.AllowsTriggerLearning(code));
}

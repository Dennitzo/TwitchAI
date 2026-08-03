using System.Text.Json;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class ModerationStageContractTests
{
    [Fact]
    public void ClassificationSchemaOmitsReasonGuidanceAndTriggers()
    {
        var envelope = Envelope(Message("m1", "farm auf d4"));

        var compiled = new ModerationPromptCompiler().CompileClassification(
            new ModerationClassificationRequest(envelope, []));
        var schema = compiled.ResponseFormat.GetRawText();

        Assert.Equal(
            ModerationPromptCompiler.ClassificationRequestEnvelopeVersion,
            compiled.ContractVersion);
        Assert.Equal(
            ModerationPromptCompiler.ClassificationResponseEnvelopeVersion,
            compiled.ResponseContractVersion);
        Assert.DoesNotContain("violationReason", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("guidance", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("triggerPhrases", schema, StringComparison.Ordinal);
        Assert.Contains("evidenceKind", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplyOnlyClassificationSchemaHasNoModerationVariant()
    {
        var envelope = Envelope(Message("m1", "wann geht es los")) with
        {
            ReplyTemplates = [new ChatReplyTemplatePrompt("reply-1", "wann geht es los")],
        };

        var compiled = new ModerationPromptCompiler().CompileClassification(
            new ModerationClassificationRequest(envelope, ["m1"]));
        using var document = JsonDocument.Parse(compiled.ResponseFormat.GetRawText());
        var definition = document.RootElement
            .GetProperty("json_schema")
            .GetProperty("schema")
            .GetProperty("$defs")
            .EnumerateObject()
            .Single()
            .Value;
        var variants = definition.GetProperty("oneOf").EnumerateArray().ToArray();

        Assert.Equal(2, variants.Length);
        Assert.Equal("null", variants[0].GetProperty("type").GetString());
        Assert.True(
            variants[1]
                .GetProperty("properties")
                .TryGetProperty("replyTemplateId", out _));
    }

    [Fact]
    public void ClassificationValidatorAcceptsActionableDecisionWithoutReason()
    {
        var envelope = Envelope(Message("m1", "farm auf d4"));
        var request = new ModerationClassificationRequest(envelope, []);
        var raw = JsonSerializer.Serialize(
            new
            {
                envelopeVersion =
                    ModerationPromptCompiler.ClassificationResponseEnvelopeVersion,
                batchId = envelope.BatchId,
                decisions = new Dictionary<string, object?>
                {
                    ["m1"] = new
                    {
                        action = "timeout",
                        timeoutSeconds = 600,
                        ruleCode = ModerationRuleCodes.Backseatgaming,
                        evidenceKind = "instruction",
                        confidence = 0.91,
                    },
                },
            });

        var result = new ModerationEnvelopeValidator().ValidateClassification(
            raw,
            request);

        Assert.True(result.IsValid);
        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal(ModerationActionKind.Timeout, decision.Action);
        Assert.Equal(ModerationRuleCodes.Backseatgaming, decision.RuleCode);
    }

    [Theory]
    [InlineData("farm auf d4")]
    [InlineData("use your heal spell")]
    [InlineData("geh hintn")]
    [InlineData("du musst nachladen")]
    public void ConfirmationValidatorAcceptsConciseBackseatgamingEvidence(
        string messageText)
    {
        var envelope = Envelope(Message("m1", messageText));
        var candidate = Candidate("m1");
        var request = new ModerationConfirmationRequest(envelope, [candidate]);
        var raw = ConfirmationEnvelope(
            envelope.BatchId,
            new Dictionary<string, object?>
            {
                ["m1"] = Confirmation(
                    "instruction",
                    messageText,
                    "Unaufgefordertes Backseatgaming an den Streamer.",
                    "Spielhinweise künftig bitte vermeiden."),
            });

        var result = new ModerationEnvelopeValidator().ValidateConfirmation(
            raw,
            request);

        Assert.True(result.IsEnvelopeValid);
        Assert.Empty(result.Issues);
        Assert.Empty(result.RejectedMessageIds);
        Assert.Single(result.ConfirmedDecisions);
    }

    [Fact]
    public void ConfirmationValidatorRejectsOnlyInvalidCandidateWithoutRepair()
    {
        var envelope = Envelope(
            Message("m1", "farm auf d4"),
            Message("m2", "du musst nachladen"));
        var request = new ModerationConfirmationRequest(
            envelope,
            [Candidate("m1"), Candidate("m2")]);
        var raw = ConfirmationEnvelope(
            envelope.BatchId,
            new Dictionary<string, object?>
            {
                ["m1"] = Confirmation(
                    "instruction",
                    "farm auf d4",
                    "Unaufgefordertes Backseatgaming an den Streamer.",
                    "Du sollst Spielhinweise vermeiden."),
                ["m2"] = Confirmation(
                    "instruction",
                    "du musst nachladen",
                    "Unaufgefordertes Backseatgaming an den Streamer.",
                    "Spielhinweise künftig bitte vermeiden."),
            });

        var result = new ModerationEnvelopeValidator().ValidateConfirmation(
            raw,
            request);

        Assert.True(result.IsEnvelopeValid);
        Assert.Equal("m2", Assert.Single(result.ConfirmedDecisions).TemporaryMessageId);
        Assert.Equal("m1", Assert.Single(result.RejectedMessageIds));
        Assert.Contains(result.Issues, issue => issue.Code == "guidance_invalid");
    }

    [Theory]
    [InlineData("soll ich auf d4 farmen?")]
    [InlineData("er schrieb farm auf d4")]
    [InlineData("nachladen")]
    public void ConfirmationValidatorAcceptsNullForNonDirectiveBoundary(string text)
    {
        var envelope = Envelope(Message("m1", text));
        var request = new ModerationConfirmationRequest(envelope, [Candidate("m1")]);
        var raw = ConfirmationEnvelope(
            envelope.BatchId,
            new Dictionary<string, object?> { ["m1"] = null });

        var result = new ModerationEnvelopeValidator().ValidateConfirmation(
            raw,
            request);

        Assert.True(result.IsEnvelopeValid);
        Assert.Empty(result.Issues);
        Assert.Empty(result.ConfirmedDecisions);
        Assert.Equal("m1", Assert.Single(result.RejectedMessageIds));
    }

    private static ModerationRequestEnvelopeV3 Envelope(
        params ModerationRequestMessageV3[] messages) =>
        new(
            ModerationPromptCompiler.RequestEnvelopeVersion,
            Guid.NewGuid(),
            "game-1",
            "Palworld",
            [new PolicySetRevision(PolicySetCode.Backseatgaming, 1)],
            [BackseatRule()],
            messages,
            [],
            []);

    private static ModerationRequestMessageV3 Message(string id, string text) =>
        new(id, text, [ModerationRuleCodes.Backseatgaming])
        {
            LocalPhraseMatches =
            [
                new ModerationRequestPhraseMatchV1(
                    1,
                    ModerationRuleCodes.Backseatgaming,
                    text),
            ],
        };

    private static ModerationRuleDefinition BackseatRule() =>
        new(
            ModerationRuleCodes.Backseatgaming,
            PolicySetCode.Backseatgaming,
            "Backseatgaming",
            ModerationRuleCodes.BackseatgamingPromptInstruction,
            ModerationActionKind.Timeout,
            600,
            "Unaufgefordertes Backseatgaming.",
            Priority: 10);

    private static ModerationConfirmationCandidate Candidate(string id) =>
        new(
            id,
            ModerationActionKind.Timeout,
            600,
            PolicySetCode.Backseatgaming,
            ModerationRuleCodes.Backseatgaming,
            "instruction",
            null,
            [],
            0.9,
            ModerationCandidateSource.ManualBlacklistPhrase);

    private static object Confirmation(
        string evidenceKind,
        string trigger,
        string violationReason,
        string guidance) =>
        new
        {
            evidenceKind,
            violationReason,
            guidance,
            triggerPhrases = new[] { trigger },
            confidence = 0.92,
        };

    private static string ConfirmationEnvelope(
        Guid batchId,
        Dictionary<string, object?> decisions) =>
        JsonSerializer.Serialize(
            new
            {
                envelopeVersion =
                    ModerationPromptCompiler.ConfirmationResponseEnvelopeVersion,
                batchId,
                decisions,
            });
}

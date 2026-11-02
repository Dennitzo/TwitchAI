using System.Collections.Concurrent;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Pipeline;
using StoredPolicy = TwitchAI.Infrastructure.Persistence.EffectivePolicySnapshot;

namespace TwitchAI.Tests.Infrastructure;

public sealed class LearningTestServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_UsesOfflineGeneralAndBackseatPolicyForWhitelistedExample()
    {
        var testIdFromRequest = Guid.Empty;
        var localAi = new FakeLocalAiClient(request =>
        {
            testIdFromRequest = ReadTestId(request.UserPrompt);
            return Completion(Allow(testIdFromRequest, "G_OTHER_ABUSE"));
        });
        var monitor = new LocalAiRunActivityMonitor();
        var activities = new List<LocalAiRunActivity>();
        monitor.ActivityChanged += (_, args) => activities.Add(args.Activity);
        var sut = CreateService(localAi, monitor: monitor);

        var result = await sut.AnalyzeAsync(new LearningTestRequest("KEKW"));

        Assert.Equal(LearningVerdict.Allow, result.Verdict);
        Assert.Equal("kekw", result.LowerText);
        Assert.Equal("KEKW", result.OriginalText);
        Assert.Equal(ModerationActionKind.None, result.ProposedAction);
        Assert.Null(result.ProposedTimeoutSeconds);
        Assert.Empty(result.ProposedReason);
        Assert.Equal("G_OTHER_ABUSE", result.ProposedRuleCode);
        Assert.Contains(
            result.ActiveRules,
            item => item.PolicySet == PolicySetCode.General);
        Assert.Contains(
            result.ActiveRules,
            item => item.PolicySet == PolicySetCode.Backseatgaming);
        var sent = Assert.Single(localAi.Requests);
        Assert.Equal(LocalAiResponseSchemaKind.LearningTest, sent.ResponseSchema);
        Assert.Equal(LocalAiRunKind.LearningTest, sent.RunKind);
        Assert.DoesNotContain("KEKW", sent.UserPrompt, StringComparison.Ordinal);
        var queued = Assert.Single(activities);
        Assert.Equal(LocalAiRunState.Queued, queued.State);
        Assert.Equal(LocalAiRunKind.LearningTest, queued.RunKind);
        Assert.StartsWith("learning-test:", queued.CorrelationId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsync_ModeratePreservesValidActionAndContextualReason()
    {
        var localAi = new FakeLocalAiClient(request =>
        {
            var id = ReadTestId(request.UserPrompt);
            return Completion(
                Response(
                    id,
                    "moderate",
                    "timeout",
                    120,
                    "G",
                    "G_SPAM",
                    "Der Chat wurde durch mehrfache Wiederholungen überflutet.",
                    ["spam spam"],
                    0.91));
        });
        var sut = CreateService(localAi);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("SPAM SPAM jetzt"));

        Assert.Equal(LearningVerdict.Moderate, result.Verdict);
        Assert.Equal(ModerationActionKind.Timeout, result.ProposedAction);
        Assert.Equal(120, result.ProposedTimeoutSeconds);
        Assert.Equal("G_SPAM", result.ProposedRuleCode);
        Assert.Equal(
            "Der Chat wurde durch mehrfache Wiederholungen überflutet. Chatregeln künftig bitte beachten.",
            result.ProposedReason);
        Assert.Equal(["spam spam"], result.ProposedTriggerPhrases);
        Assert.False(result.UsedRuleFallback);
    }

    [Fact]
    public async Task AnalyzeAsync_NormalizesGuidanceAfterRepairRepeatsInvalidDirectAddress()
    {
        var calls = 0;
        var localAi = new FakeLocalAiClient(request =>
        {
            calls++;
            return Completion(Response(
                ReadTestId(request.UserPrompt),
                "moderate",
                "timeout",
                600,
                "B",
                ModerationRuleCodes.Backseatgaming,
                "Der Streamer wurde ungefragt zum Nachladen angewiesen.",
                ["du musst nachladen"],
                0.94,
                evidenceKind: "instruction",
                guidanceOverride: "Du solltest Spielhinweise vermeiden."));
        });
        var sut = CreateService(localAi);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Du musst nachladen"));

        Assert.Equal(2, calls);
        Assert.Equal(LearningVerdict.Moderate, result.Verdict);
        Assert.Equal(
            "Der Streamer wurde ungefragt zum Nachladen angewiesen. Spielhinweise künftig bitte vermeiden.",
            result.ProposedReason);
    }

    [Fact]
    public async Task AnalyzeAsync_PersonalAttackPreservesTenMinuteTimeout()
    {
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "G",
                    ModerationRuleCodes.PersonalAttack,
                    "Nutzer oder Streamer wurde direkt persönlich beleidigt.",
                    ["du idiot"],
                    0.94)));
        var sut = CreateService(localAi);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Du Idiot"));

        Assert.Equal(LearningVerdict.Moderate, result.Verdict);
        Assert.Equal(ModerationActionKind.Timeout, result.ProposedAction);
        Assert.Equal(600, result.ProposedTimeoutSeconds);
        Assert.Equal(ModerationRuleCodes.PersonalAttack, result.ProposedRuleCode);
        Assert.Equal(
            "Nutzer oder Streamer wurde direkt persönlich beleidigt. Chatregeln künftig bitte beachten.",
            result.ProposedReason);
    }

    [Fact]
    public async Task AnalyzeAsync_DeleteForSpamRemainsDelete()
    {
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "delete",
                    null,
                    "G",
                    ModerationRuleCodes.Spam,
                    "Der Chat wurde durch mehrfache Wiederholungen überflutet.",
                    ["spam spam"],
                    0.91)));
        var sut = CreateService(localAi);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("SPAM SPAM"));

        Assert.Equal(ModerationActionKind.Delete, result.ProposedAction);
        Assert.Null(result.ProposedTimeoutSeconds);
        Assert.Equal(ModerationRuleCodes.Spam, result.ProposedRuleCode);
    }

    [Fact]
    public async Task AnalyzeAsync_BackseatgamingPreservesTenMinuteTimeout()
    {
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "B",
                    ModerationRuleCodes.Backseatgaming,
                    "Der Streamer wurde ungefragt zu einer Spielhandlung angewiesen.",
                    ["geh nach links"],
                    0.93)));
        var sut = CreateService(localAi);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Geh nach links"));

        Assert.Equal(ModerationActionKind.Timeout, result.ProposedAction);
        Assert.Equal(600, result.ProposedTimeoutSeconds);
        Assert.Equal(
            ModerationRuleCodes.Backseatgaming,
            result.ProposedRuleCode);
    }

    [Fact]
    public async Task AnalyzeAsync_BotScamPreservesBan()
    {
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "ban",
                    null,
                    "G",
                    ModerationRuleCodes.BotScam,
                    "Der eigene Kanal wurde ausdrücklich beworben.",
                    ["meinen kanal"],
                    0.97)));
        var sut = CreateService(localAi);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Besucht meinen Kanal"));

        Assert.Equal(ModerationActionKind.Ban, result.ProposedAction);
        Assert.Null(result.ProposedTimeoutSeconds);
        Assert.Equal(
            ModerationRuleCodes.BotScam,
            result.ProposedRuleCode);
    }

    [Fact]
    public async Task AnalyzeAsync_BeggingPreservesDayTimeout()
    {
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    86_400,
                    "G",
                    ModerationRuleCodes.Begging,
                    "Ausdrückliches Betteln um einen kostenlosen Sub.",
                    ["schenk mir bitte einen sub"],
                    0.95)));
        var sut = CreateService(localAi);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Schenk mir bitte einen Sub"));

        Assert.Equal(ModerationActionKind.Timeout, result.ProposedAction);
        Assert.Equal(86_400, result.ProposedTimeoutSeconds);
        Assert.Equal(
            ModerationRuleCodes.Begging,
            result.ProposedRuleCode);
    }

    [Fact]
    public async Task AnalyzeAsync_HarmPreservesDayTimeout()
    {
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    86_400,
                    "G",
                    ModerationRuleCodes.Harm,
                    "Eine Person wurde mit einer konkreten Drohung eingeschüchtert.",
                    ["unnötiger lärm"],
                    0.83)));
        var sut = CreateService(localAi);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Unnötiger Lärm"));

        Assert.Equal(ModerationActionKind.Timeout, result.ProposedAction);
        Assert.Equal(86_400, result.ProposedTimeoutSeconds);
        Assert.Equal(ModerationRuleCodes.Harm, result.ProposedRuleCode);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingRuleFallsBackToGeneralBaseRuleBeforeRepair()
    {
        var localAi = new FakeLocalAiClient(request =>
        {
            var id = ReadTestId(request.UserPrompt);
            return Completion(
                Response(
                    id,
                    "allow",
                    "none",
                    null,
                    "G",
                    null,
                    string.Empty,
                    [],
                    0.4));
        });
        var diagnostics = new RecordingValidationAttemptStore();
        var sut = CreateService(localAi, validationAttempts: diagnostics);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Eine harmlose Testnachricht"));

        Assert.Single(localAi.Requests);
        Assert.Equal("G_OTHER_ABUSE", result.ProposedRuleCode);
        Assert.Equal(PolicySetCode.General, result.ProposedPolicySet);
        Assert.True(result.UsedRuleFallback);
        Assert.Collection(
            diagnostics.Attempts,
            initial =>
            {
                Assert.Equal(AiValidationAttemptKind.Initial, initial.AttemptKind);
                Assert.Equal(AiValidationOutcome.Rejected, initial.Outcome);
                Assert.Equal(["rule_missing"], initial.ValidationErrorCodes);
            },
            fallback =>
            {
                Assert.Equal(AiValidationAttemptKind.LocalFallback, fallback.AttemptKind);
                Assert.Equal(AiValidationOutcome.Fallback, fallback.Outcome);
                Assert.NotNull(fallback.ParentCorrelationId);
                Assert.Equal(AiValidationResponseFingerprint.None, fallback.Response);
            });
    }

    [Fact]
    public async Task AnalyzeAsync_UnknownExplicitRuleRemainsInvalidAfterOneRepair()
    {
        var localAi = new FakeLocalAiClient(request =>
        {
            var id = ReadTestId(request.UserPrompt);
            return Completion(
                Response(
                    id,
                    "allow",
                    "none",
                    null,
                    "G",
                    "G_UNKNOWN",
                    string.Empty,
                    [],
                    0.4));
        });
        var sut = CreateService(localAi);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await sut.AnalyzeAsync(
                new LearningTestRequest("Eine harmlose Testnachricht")));

        Assert.Equal(2, localAi.Requests.Count);
        var repair = localAi.Requests[1];
        Assert.EndsWith("-repair", repair.CorrelationId, StringComparison.Ordinal);
        Assert.NotEqual(localAi.Requests[0].CorrelationId, repair.CorrelationId);
        Assert.Contains(
            "\"validationErrorCodes\":[\"rule_unknown\"]",
            repair.UserPrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain("invalidResponse", repair.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("validationErrors", repair.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("activePolicySets", repair.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("publicReason", repair.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsync_ExplicitInvalidPolicyIsNeverSilentlyReplaced()
    {
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "allow",
                    "none",
                    null,
                    "X",
                    "G_OTHER_ABUSE",
                    string.Empty,
                    [],
                    0.4)));
        var sut = CreateService(localAi);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await sut.AnalyzeAsync(
                new LearningTestRequest("Eine harmlose Testnachricht")));

        Assert.Contains("policy_set_invalid", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, localAi.Requests.Count);
    }

    [Fact]
    public async Task AnalyzeAsync_BackseatQuestionIsRejectedAfterSingleRepair()
    {
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "B",
                    ModerationRuleCodes.Backseatgaming,
                    "Der Streamer wurde ungefragt zu einer Spielhandlung angewiesen.",
                    ["geh nach links"],
                    0.9)));
        var sut = CreateService(localAi);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await sut.AnalyzeAsync(
                new LearningTestRequest("Geh nach links?")));

        Assert.Contains(
            "backseat_context_insufficient",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(2, localAi.Requests.Count);
    }

    [Fact]
    public async Task AnalyzeAsync_BackseatRequiresConcreteMultiwordTrigger()
    {
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "B",
                    ModerationRuleCodes.Backseatgaming,
                    "Unaufgeforderter Spielhinweis.",
                    ["links"],
                    0.9)));
        var sut = CreateService(localAi);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await sut.AnalyzeAsync(
                new LearningTestRequest("Geh nach links")));

        Assert.Contains(
            "backseat_evidence_insufficient",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(2, localAi.Requests.Count);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingRuleUsesMatchingLocalBackseatRuleBeforeRepair()
    {
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "B",
                    null,
                    "Der Streamer wurde ungefragt zu einer Spielhandlung angewiesen.",
                    ["geh nach links"],
                    0.9,
                    evidenceKind: "instruction")));
        var phraseSource = new StaticPhraseRuleSource(
        [
            new PhraseRule(
                1,
                ModerationRuleCodes.Backseatgaming,
                PolicySetCode.Backseatgaming,
                "geh nach links",
                PhraseManualOverride.Active,
                1,
                0,
                IsArchived: false),
        ]);
        var sut = CreateService(localAi, phraseSource: phraseSource);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Geh nach links"));

        Assert.Single(localAi.Requests);
        Assert.Equal(ModerationRuleCodes.Backseatgaming, result.ProposedRuleCode);
        Assert.Equal(PolicySetCode.Backseatgaming, result.ProposedPolicySet);
        Assert.True(result.UsedRuleFallback);
        Assert.Equal(
            [ModerationRuleCodes.Backseatgaming],
            result.LocalMatchCodes);
    }

    [Fact]
    public async Task AnalyzeAsync_ActivePhraseUsesTrustedEvidenceWithoutAiRepair()
    {
        const string message = "ich liebe dich";
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "G",
                    ModerationRuleCodes.PersonalAttack,
                    "Der Streamer wurde unangebracht persönlich angesprochen.",
                    [message],
                    0.91,
                    evidenceKind: "sexualization")));
        var phraseSource = new StaticPhraseRuleSource(
        [
            new PhraseRule(
                173,
                ModerationRuleCodes.PersonalAttack,
                PolicySetCode.General,
                message,
                PhraseManualOverride.Active,
                0,
                0,
                IsArchived: false),
        ]);
        var sut = CreateService(localAi, phraseSource: phraseSource);

        var result = await sut.AnalyzeAsync(new LearningTestRequest(message));

        Assert.Single(localAi.Requests);
        Assert.Equal(LearningVerdict.Moderate, result.Verdict);
        Assert.Equal(ModerationRuleCodes.PersonalAttack, result.ProposedRuleCode);
        Assert.Equal("unwanted_intimate_approach", result.EvidenceKind);
        Assert.Equal("manual_blacklist_phrase", result.HumanPrioritySource);
    }

    [Fact]
    public async Task AnalyzeAsync_ExactModerateLearningUsesTrustedEvidenceWithoutAiRepair()
    {
        const string message = "ich liebe dich";
        var retrieval = new StaticRetrievalStore(
        [
            new RetrievalExample(
                message,
                ModerationActionKind.Timeout,
                600,
                PolicySetCode.General,
                "Unangebrachte persoenliche Annaeherung.",
                IsPositive: true,
                ModerationRuleCodes.PersonalAttack)
            {
                IsExactHumanDecision = true,
            },
        ]);
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "G",
                    ModerationRuleCodes.PersonalAttack,
                    "Der Streamer wurde unangebracht persönlich angesprochen.",
                    [message],
                    0.91,
                    evidenceKind: "unwanted_intimate_approach")));
        var sut = CreateService(localAi, retrieval: retrieval);

        var result = await sut.AnalyzeAsync(new LearningTestRequest(message));

        Assert.Single(localAi.Requests);
        Assert.Equal(LearningVerdict.Moderate, result.Verdict);
        Assert.Equal("unwanted_intimate_approach", result.EvidenceKind);
        Assert.Equal("exact_moderate_learning", result.HumanPrioritySource);
    }

    [Fact]
    public async Task AnalyzeAsync_HumanRuleOverrideRejectsIncompatibleAiReason()
    {
        const string message = "du idiot";
        var retrieval = new StaticRetrievalStore(
        [
            new RetrievalExample(
                message,
                ModerationActionKind.Timeout,
                600,
                PolicySetCode.General,
                "Direkte Beleidigung.",
                IsPositive: true,
                ModerationRuleCodes.PersonalAttack)
            {
                IsExactHumanDecision = true,
            },
        ]);
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    86_400,
                    "G",
                    ModerationRuleCodes.PersonalData,
                    "Private Daten wurden veröffentlicht.",
                    [],
                    0.91,
                    evidenceKind: "disclosure",
                    sensitiveDataType: "email")));
        var sut = CreateService(localAi, retrieval: retrieval);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await sut.AnalyzeAsync(new LearningTestRequest(message)));

        Assert.Single(localAi.Requests);
        Assert.Contains(
            "contextual reason for the human-priority rule",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsync_ExactAllowLearningOverridesInvalidAiEvidence()
    {
        const string message = "ich liebe dich";
        var retrieval = new StaticRetrievalStore(
        [
            new RetrievalExample(
                message,
                ModerationActionKind.None,
                null,
                PolicySetCode.General,
                "Im konkreten Kontext erlaubt.",
                IsPositive: false,
                ModerationRuleCodes.PersonalAttack)
            {
                IsExactHumanDecision = true,
            },
        ]);
        var localAi = new FakeLocalAiClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "G",
                    ModerationRuleCodes.PersonalAttack,
                    "Unangebrachte persoenliche Annaeherung.",
                    [message],
                    0.91,
                    evidenceKind: "sexualization")));
        var sut = CreateService(localAi, retrieval: retrieval);

        var result = await sut.AnalyzeAsync(new LearningTestRequest(message));

        Assert.Single(localAi.Requests);
        Assert.Equal(LearningVerdict.Allow, result.Verdict);
        Assert.Equal(ModerationActionKind.None, result.ProposedAction);
        Assert.Null(result.EvidenceKind);
        Assert.Equal("exact_allow_learning", result.HumanPrioritySource);
    }

    [Fact]
    public async Task AnalyzeAsync_SemanticEvidenceMismatchRepairsWithExplicitRuleMapping()
    {
        var localAi = new FakeLocalAiClient(request =>
        {
            var isRepair = request.CorrelationId.EndsWith(
                "-repair",
                StringComparison.Ordinal);
            return
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "G",
                    ModerationRuleCodes.PersonalAttack,
                    isRepair
                        ? "Nutzer oder Streamer wurde direkt persönlich beleidigt."
                        : "RAW-REASON-MUST-NOT-BE-COPIED",
                    ["du idiot"],
                    0.91,
                    evidenceKind: isRepair
                        ? "direct_insult"
                        : "sexualization"));
        });
        var sut = CreateService(localAi);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Du Idiot"));

        Assert.Equal(2, localAi.Requests.Count);
        var repair = localAi.Requests[1];
        Assert.Contains(
            "\"validationErrorCodes\":[\"evidence_kind_invalid\"]",
            repair.UserPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"allowedEvidenceKindsByRule\"",
            repair.UserPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"invalidDecisionSummary\":{\"verdict\":\"moderate\",\"selectedRuleCode\":\"G_PERSONAL_ATTACK\",\"evidenceKind\":\"sexualization\"}",
            repair.UserPrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RAW-REASON-MUST-NOT-BE-COPIED",
            repair.UserPrompt,
            StringComparison.Ordinal);
        Assert.Contains("direct_insult", repair.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("verified_harassment", repair.UserPrompt, StringComparison.Ordinal);
        Assert.Equal("direct_insult", result.EvidenceKind);
        Assert.Null(result.HumanPrioritySource);
    }

    [Fact]
    public async Task AnalyzeAsync_EvidenceRepairCannotSilentlyChangeRuleCode()
    {
        var localAi = new FakeLocalAiClient(request =>
        {
            var isRepair = request.CorrelationId.EndsWith(
                "-repair",
                StringComparison.Ordinal);
            return Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "G",
                    isRepair
                        ? ModerationRuleCodes.Hate
                        : ModerationRuleCodes.PersonalAttack,
                    isRepair
                        ? "Eine geschützte Gruppe wurde mit Hassrede abgewertet."
                        : "Nutzer oder Streamer wurde direkt persönlich beleidigt.",
                    ["du idiot"],
                    0.91,
                    evidenceKind: isRepair ? "racist" : "sexualization"));
        });
        var sut = CreateService(localAi);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await sut.AnalyzeAsync(
                new LearningTestRequest("Du Idiot")));

        Assert.Equal(2, localAi.Requests.Count);
        Assert.Contains(
            "repair_rule_changed",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsync_RecordsInitialAndRepairDiagnosticsWithoutContent()
    {
        var localAi = new FakeLocalAiClient(request =>
        {
            var id = ReadTestId(request.UserPrompt);
            return request.CorrelationId.EndsWith("-repair", StringComparison.Ordinal)
                ? Completion(Allow(id, "G_OTHER_ABUSE"))
                : Completion(
                    Response(
                        id,
                        "allow",
                        "none",
                        null,
                        "G",
                        "G_UNKNOWN",
                        string.Empty,
                        [],
                        0.4));
        });
        var diagnostics = new RecordingValidationAttemptStore();
        var sut = CreateService(localAi, validationAttempts: diagnostics);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Ein vertraulicher Beispieltext"));

        Assert.Equal(LearningVerdict.Allow, result.Verdict);
        Assert.Collection(
            diagnostics.Attempts,
            initial =>
            {
                Assert.Equal(AiValidationAttemptKind.Initial, initial.AttemptKind);
                Assert.Equal(AiValidationOutcome.Rejected, initial.Outcome);
                Assert.Equal(["rule_unknown"], initial.ValidationErrorCodes);
                Assert.Null(initial.ParentCorrelationId);
                Assert.NotNull(initial.Response.Sha256);
                Assert.True(initial.PromptCharacters.Schema > 0);
            },
            repair =>
            {
                Assert.Equal(AiValidationAttemptKind.Repair, repair.AttemptKind);
                Assert.Equal(AiValidationOutcome.Repaired, repair.Outcome);
                Assert.Equal(initialCorrelation(diagnostics.Attempts), repair.ParentCorrelationId);
                Assert.DoesNotContain(
                    "Ein vertraulicher Beispieltext",
                    System.Text.Json.JsonSerializer.Serialize(repair),
                    StringComparison.OrdinalIgnoreCase);
            });

        static string initialCorrelation(IReadOnlyList<AiValidationAttempt> attempts) =>
            attempts[0].CorrelationId;
    }

    [Fact]
    public async Task AnalyzeAsync_IncludesBoundedLowercaseCurrentLearningExamples()
    {
        var retrieval = new StaticRetrievalStore(
        [
            new RetrievalExample(
                "DAS IST HARMLOS",
                ModerationActionKind.None,
                null,
                PolicySetCode.General,
                string.Empty,
                IsPositive: false,
                "G_SPAM"),
        ]);
        var localAi = new FakeLocalAiClient(request =>
            Completion(Allow(ReadTestId(request.UserPrompt), "G_OTHER_ABUSE")));
        var sut = CreateService(localAi, retrieval: retrieval);

        await sut.AnalyzeAsync(new LearningTestRequest("das ist harmlos"));

        var sent = Assert.Single(localAi.Requests);
        Assert.Contains("\"learningExamples\":[", sent.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"das ist harmlos\"", sent.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("\"matchedPhrase\":\"das ist harmlos\"", sent.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("DAS IST HARMLOS", sent.UserPrompt, StringComparison.Ordinal);
        Assert.Equal(1, retrieval.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_RejectsResultWhenPolicyChangedDuringInference()
    {
        var original = Policy("policy-1");
        var changed = Policy("policy-2");
        var store = new FakePolicyStore(original, changed);
        var localAi = new FakeLocalAiClient(request =>
            Completion(Allow(ReadTestId(request.UserPrompt), "G_OTHER_ABUSE")));
        var sut = CreateService(localAi, store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.AnalyzeAsync(
                new LearningTestRequest("harmlos")));

        Assert.Contains("erneut starten", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsync_RejectsResultWhenLearningChangedDuringInference()
    {
        var revision = new LearningContextRevision();
        var localAi = new FakeLocalAiClient(request =>
        {
            revision.Advance();
            return Completion(Allow(ReadTestId(request.UserPrompt), "G_OTHER_ABUSE"));
        });
        var sut = CreateService(localAi, learningRevision: revision);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.AnalyzeAsync(
                new LearningTestRequest("harmlos")));

        Assert.Contains("Learning-Kontext", exception.Message, StringComparison.Ordinal);
        Assert.Contains("erneut starten", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsPolicyCurrentAsync_RejectsCompletedResultAfterLearningChange()
    {
        var revision = new LearningContextRevision();
        var localAi = new FakeLocalAiClient(request =>
            Completion(Allow(ReadTestId(request.UserPrompt), "G_OTHER_ABUSE")));
        var sut = CreateService(localAi, learningRevision: revision);
        var result = await sut.AnalyzeAsync(new LearningTestRequest("harmlos"));
        Assert.True(await sut.IsPolicyCurrentAsync(result));

        revision.Advance();

        Assert.False(await sut.IsPolicyCurrentAsync(result));
    }

    [Fact]
    public async Task AnalyzeAsync_AllowsOnlyOneRunningTest()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var localAi = new FakeLocalAiClient(async request =>
        {
            entered.SetResult();
            await release.Task;
            return Completion(Allow(ReadTestId(request.UserPrompt), "G_OTHER_ABUSE"));
        });
        var sut = CreateService(localAi);
        var first = sut.AnalyzeAsync(new LearningTestRequest("erste")).AsTask();
        await entered.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.AnalyzeAsync(new LearningTestRequest("zweite")));

        release.SetResult();
        await first;
    }

    [Fact]
    public async Task IsPolicyCurrentAsync_ComparesLearningPolicyFingerprint()
    {
        var currentPolicy = Policy("policy-current");
        var store = new FakePolicyStore(currentPolicy);
        var sut = CreateService(
            new FakeLocalAiClient(
                _ => Task.FromException<LocalAiModerationCompletion>(
                    new NotSupportedException())),
            store);
        var result = Result(
            LearningPolicyRevisionFingerprint.Create(currentPolicy));

        Assert.True(await sut.IsPolicyCurrentAsync(result));
        Assert.False(
                await CreateService(
                    new FakeLocalAiClient(
                        _ => Task.FromException<LocalAiModerationCompletion>(
                            new NotSupportedException())),
                    new FakePolicyStore(Policy("different")))
                .IsPolicyCurrentAsync(result));
        Assert.False(
                await CreateService(
                    new FakeLocalAiClient(
                        _ => Task.FromException<LocalAiModerationCompletion>(
                            new NotSupportedException())),
                    new FakePolicyStore(
                        currentPolicy with
                        {
                            GeneralRevision = currentPolicy.GeneralRevision + 1,
                        }))
                .IsPolicyCurrentAsync(result));
    }

    [Fact]
    public async Task GenerateReasonAsync_UsesLowercaseTextAndSelectedRuleOnly()
    {
        var policy = Policy("policy-reason");
        var localAi = new FakeLocalAiClient(
            _ => Completion(
                    "{\"violationReason\":\"Der Streamer wurde ungefragt zum Nachladen angewiesen.\",\"guidance\":\"Spielhinweise künftig bitte vermeiden.\"}"));
        var sut = CreateService(localAi, new FakePolicyStore(policy));
        var testResult = Result(
            LearningPolicyRevisionFingerprint.Create(policy)) with
        {
            OriginalText = "DU MUSST NACHLADEN",
            LowerText = "du musst nachladen",
        };

        var result = await sut.GenerateReasonAsync(
            new LearningReasonRequest(
                testResult,
                ModerationRuleCodes.Backseatgaming,
                ModerationActionKind.Timeout,
                600));

        Assert.Equal(ModerationRuleCodes.Backseatgaming, result.RuleCode);
        Assert.Equal(
            "Der Streamer wurde ungefragt zum Nachladen angewiesen. Spielhinweise künftig bitte vermeiden.",
            result.Reason);
        Assert.Equal(
            "Der Streamer wurde ungefragt zum Nachladen angewiesen.",
            result.ViolationReason);
        Assert.Equal(
            "Spielhinweise künftig bitte vermeiden.",
            result.Guidance);
        var request = Assert.Single(localAi.Requests);
        Assert.Equal(LocalAiRunKind.LearningTest, request.RunKind);
        Assert.NotNull(request.ResponseFormat);
        Assert.Contains("du musst nachladen", request.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("DU MUSST NACHLADEN", request.UserPrompt, StringComparison.Ordinal);
        Assert.Contains(
            ModerationRuleCodes.Backseatgaming,
            request.UserPrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ModerationRuleCodes.PersonalAttack,
            request.UserPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateReasonAsync_RepairsInvalidGuidanceOnce()
    {
        var policy = Policy("policy-reason-repair");
        var responseNumber = 0;
        var localAi = new FakeLocalAiClient(
            _ => Completion(
                Interlocked.Increment(ref responseNumber) == 1
                    ? "{\"violationReason\":\"Der Streamer wurde ungefragt zum Nachladen angewiesen.\",\"guidance\":\"Du solltest Spielhinweise vermeiden.\"}"
                    : "{\"violationReason\":\"Der Streamer wurde ungefragt zum Nachladen angewiesen.\",\"guidance\":\"Spielhinweise künftig bitte vermeiden.\"}"));
        var sut = CreateService(localAi, new FakePolicyStore(policy));

        var result = await sut.GenerateReasonAsync(
            new LearningReasonRequest(
                Result(LearningPolicyRevisionFingerprint.Create(policy)) with
                {
                    OriginalText = "DU MUSST NACHLADEN",
                    LowerText = "du musst nachladen",
                },
                ModerationRuleCodes.Backseatgaming,
                ModerationActionKind.Timeout,
                600));

        Assert.Equal(
            "Der Streamer wurde ungefragt zum Nachladen angewiesen. Spielhinweise künftig bitte vermeiden.",
            result.Reason);
        Assert.Equal(2, localAi.Requests.Count);
        Assert.EndsWith("-repair", localAi.Requests[1].CorrelationId, StringComparison.Ordinal);
        Assert.Contains(
            "invalidOutput",
            localAi.Requests[1].UserPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateReasonAsync_SecondInvalidResponseUsesNoFallback()
    {
        var policy = Policy("policy-reason-invalid");
        var localAi = new FakeLocalAiClient(
            _ => Completion(
                "{\"violationReason\":\"Allgemeiner Verstoß.\",\"guidance\":\"Du solltest das künftig vermeiden.\"}"));
        var sut = CreateService(localAi, new FakePolicyStore(policy));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => sut.GenerateReasonAsync(
                    new LearningReasonRequest(
                        Result(LearningPolicyRevisionFingerprint.Create(policy)),
                        ModerationRuleCodes.Backseatgaming,
                        ModerationActionKind.Timeout,
                        600))
                .AsTask());

        Assert.Equal(2, localAi.Requests.Count);
    }

    [Fact]
    public async Task GenerateReasonAsync_DeleteUsesEmptyStructuredParts()
    {
        var policy = Policy("policy-reason-delete");
        var localAi = new FakeLocalAiClient(
            _ => Completion("{\"violationReason\":\"\",\"guidance\":\"\"}"));
        var sut = CreateService(localAi, new FakePolicyStore(policy));

        var result = await sut.GenerateReasonAsync(
            new LearningReasonRequest(
                Result(LearningPolicyRevisionFingerprint.Create(policy)),
                ModerationRuleCodes.Backseatgaming,
                ModerationActionKind.Delete,
                null));

        Assert.Empty(result.Reason);
        var properties = Assert.Single(localAi.Requests).ResponseFormat!.Value
            .GetProperty("json_schema")
            .GetProperty("schema")
            .GetProperty("properties");
        Assert.Equal(
            string.Empty,
            properties.GetProperty("violationReason").GetProperty("const").GetString());
        Assert.Equal(
            string.Empty,
            properties.GetProperty("guidance").GetProperty("const").GetString());
    }

    private static LearningTestService CreateService(
        ILocalAiClient client,
        IPolicyStore? store = null,
        ILocalAiRunActivityMonitor? monitor = null,
        IPhraseRuleSource? phraseSource = null,
        IRetrievalExampleStore? retrieval = null,
        ILearningContextRevision? learningRevision = null,
        IAiValidationAttemptStore? validationAttempts = null) =>
        new(
            store ?? new FakePolicyStore(Policy("policy-1")),
            new TwitchAI.Core.Services.ChatTextNormalizer(),
            phraseSource ?? new EmptyPhraseRuleSource(),
            new BlacklistMatcher(),
            client,
            new InfrastructureOptions(),
            monitor,
            timeProvider: null,
            retrieval,
            learningRevision,
            validationAttempts);

    private static StoredPolicy Policy(string hash)
    {
        var now = DateTimeOffset.UtcNow;
        return new StoredPolicy(
            GlobalPolicyStorage.BroadcasterId,
            CategoryId: null,
            GeneralRevision: 3,
            CategoryRevision: 3,
            hash,
            "prompt",
            [
                StoredRule(
                    1,
                    "G_OTHER_ABUSE",
                    "Sonstiger Verstoß",
                    "Allgemeiner Verstoß.",
                    100,
                    ModerationRuleAction.Timeout,
                    600,
                    now),
                StoredRule(
                    2,
                    ModerationRuleCodes.Spam,
                    "Spam",
                    "Wiederholter Spam.",
                    80,
                    ModerationRuleAction.Timeout,
                    120,
                    now),
                StoredRule(
                    3,
                    ModerationRuleCodes.Backseatgaming,
                    "Backseatgaming",
                    "Unaufgeforderter Spielhinweis.",
                    100,
                    ModerationRuleAction.Timeout,
                    600,
                    now),
                StoredRule(
                    4,
                    ModerationRuleCodes.PersonalAttack,
                    "Beleidigung",
                    "Direkte Beleidigung eines Nutzers oder Streamers.",
                    70,
                    ModerationRuleAction.Timeout,
                    600,
                    now),
                StoredRule(
                    5,
                    ModerationRuleCodes.BotScam,
                    "Werbung",
                    "Unaufgeforderte Werbung oder Eigenwerbung.",
                    75,
                    ModerationRuleAction.Ban,
                    null,
                    now),
                StoredRule(
                    6,
                    ModerationRuleCodes.Begging,
                    "Gebettel",
                    "Betteln nach Subs, Geschenken oder Giveaways.",
                    76,
                    ModerationRuleAction.Timeout,
                    86_400,
                    now),
                StoredRule(
                    7,
                    ModerationRuleCodes.Harm,
                    "Gewalt und Drohungen",
                    "Drohung oder Aufforderung zu Gewalt.",
                    20,
                    ModerationRuleAction.Timeout,
                    86_400,
                    now),
                StoredRule(
                    8,
                    ModerationRuleCodes.Hate,
                    "Hassrede",
                    "Hassrede oder diskriminierende Aussage.",
                    30,
                    ModerationRuleAction.Timeout,
                    600,
                    now),
                StoredRule(
                    9,
                    ModerationRuleCodes.PersonalData,
                    "Persoenliche Daten",
                    "Veroeffentlichung persoenlicher Daten.",
                    10,
                    ModerationRuleAction.Timeout,
                    86_400,
                    now),
            ],
            []);
    }

    private static BlacklistRuleRecord StoredRule(
        long id,
        string code,
        string title,
        string publicReason,
        int priority,
        ModerationRuleAction action,
        int? timeout,
        DateTimeOffset now) =>
        new(
            id,
            ScopeId: code == ModerationRuleCodes.Backseatgaming ? 2 : 1,
            code,
            title,
            "rule",
            action,
            timeout,
            publicReason,
            priority,
            Enabled: true,
            RowVersion: 1,
            now,
            now,
            ArchivedAt: null);

    private static string Allow(Guid id, string ruleCode) =>
        Response(
            id,
            "allow",
            "none",
            null,
            "G",
            ruleCode,
            string.Empty,
            [],
            0.75);

    private static string Response(
        Guid id,
        string verdict,
        string action,
        int? timeoutSeconds,
        string? policySet,
        string? ruleCode,
        string reason,
        IReadOnlyList<string> triggerPhrases,
        double confidence,
        string? evidenceKind = null,
        string? sensitiveDataType = null,
        string? guidanceOverride = null)
    {
        var resolvedEvidenceKind = evidenceKind;
        if (string.Equals(verdict, "moderate", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(resolvedEvidenceKind))
        {
            var compatibleEvidenceKinds = ModerationEvidenceKinds
                .ForRule(ruleCode);
            resolvedEvidenceKind = compatibleEvidenceKinds.Count == 0
                ? null
                : compatibleEvidenceKinds[0];
        }

        return
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                envelopeVersion = LearningTestPromptFactory.ResponseEnvelopeVersion,
                testId = id,
                decision = new
                {
                    verdict,
                    action,
                    timeoutSeconds,
                    policySet,
                    ruleCode,
                    evidenceKind = resolvedEvidenceKind,
                    sensitiveDataType,
                    violationReason = string.Equals(
                            verdict,
                            "moderate",
                            StringComparison.Ordinal)
                        && action is "timeout" or "ban"
                            ? reason
                            : string.Empty,
                    guidance = string.Equals(
                            verdict,
                            "moderate",
                            StringComparison.Ordinal)
                        && action is "timeout" or "ban"
                            ? guidanceOverride ?? "Chatregeln künftig bitte beachten."
                            : string.Empty,
                    triggerPhrases,
                    confidence,
                },
            });
    }

    private static Guid ReadTestId(string requestJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(requestJson);
        var root = document.RootElement;
        return root.TryGetProperty("testId", out var testId)
            ? testId.GetGuid()
            : root
                .GetProperty("request")
                .GetProperty("testId")
                .GetGuid();
    }

    private static LocalAiModerationCompletion Completion(string envelope) =>
        new(
            envelope,
            new LocalAiUsageMetrics(
                PromptTokens: 12,
                CompletionTokens: 8,
                TotalTokens: 20,
                ElapsedMilliseconds: 5,
                "model",
                "stop"));

    private static LearningTestResult Result(string fingerprint) =>
        new(
            Guid.NewGuid(),
            "Text",
            "text",
            LearningVerdict.Allow,
            ModerationActionKind.Timeout,
            600,
            PolicySetCode.General,
            "G_OTHER_ABUSE",
            "Regelverstoß.",
            [],
            0.5,
            [],
            1,
            fingerprint,
            [],
            "model",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            false);

    private sealed class FakePolicyStore(params StoredPolicy[] policies)
        : IPolicyStore
    {
        private readonly ConcurrentQueue<StoredPolicy> _policies = new(policies);
        private StoredPolicy _last = policies[^1];

        public Task EnsureBroadcasterSeededAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<StoredPolicy> GetEffectivePolicyAsync(
            string broadcasterId,
            string? categoryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_last);

        public Task<StoredPolicy> GetLearningPolicyAsync(
            CancellationToken cancellationToken = default)
        {
            if (_policies.TryDequeue(out var policy))
            {
                _last = policy;
            }

            return Task.FromResult(_last);
        }
    }

    private sealed class EmptyPhraseRuleSource : IPhraseRuleSource
    {
        public ValueTask<IReadOnlyList<PhraseRule>> GetRulesAsync(
            string channelId,
            TwitchAI.Core.Models.EffectivePolicySnapshot policy,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<PhraseRule>>([]);
    }

    private sealed class StaticPhraseRuleSource(
        IReadOnlyList<PhraseRule> rules) : IPhraseRuleSource
    {
        public ValueTask<IReadOnlyList<PhraseRule>> GetRulesAsync(
            string channelId,
            TwitchAI.Core.Models.EffectivePolicySnapshot policy,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(rules);
    }

    private sealed class StaticRetrievalStore(
        IReadOnlyList<RetrievalExample> examples) : IRetrievalExampleStore
    {
        public int CallCount { get; private set; }

        public ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantAsync(
            string channelId,
            string? categoryId,
            TwitchAI.Core.Models.EffectivePolicySnapshot policy,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(examples);

        public ValueTask<IReadOnlyList<RetrievalExample>>
            GetRelevantForLearningTestAsync(
                TwitchAI.Core.Models.EffectivePolicySnapshot policy,
                CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(examples);
        }
    }

    private sealed class FakeLocalAiClient : ILocalAiClient
    {
        private readonly Func<
            LocalAiModerationRequest,
            Task<LocalAiModerationCompletion>> _response;

        public FakeLocalAiClient(
            Func<LocalAiModerationRequest, LocalAiModerationCompletion> response)
            : this(request => Task.FromResult(response(request)))
        {
        }

        public FakeLocalAiClient(
            Func<
                LocalAiModerationRequest,
                Task<LocalAiModerationCompletion>> response)
        {
            _response = response;
        }

        public List<LocalAiModerationRequest> Requests { get; } = [];

        public Task<LocalAiModerationCompletion> CompleteModerationAsync(
            LocalAiModerationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return _response(request);
        }

        public Task<IReadOnlyList<string>> GetLoadedModelsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingValidationAttemptStore
        : IAiValidationAttemptStore
    {
        public List<AiValidationAttempt> Attempts { get; } = [];

        public ValueTask RecordAsync(
            AiValidationAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add(attempt);
            return ValueTask.CompletedTask;
        }
    }
}

using System.Reflection;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.App.Tests.ViewModels;

public sealed class LearningDatasetViewModelSaveTests
{
    [Fact]
    public async Task SaveAsync_EditDuringWrite_PersistsNewestEditWithoutOverwritingIt()
    {
        var initial = Row(reason: "Ausgang", rowVersion: 0);
        var dataset = new BlockingDatasetService(initial);
        using var viewModel = new LearningDatasetViewModel(
            dataset,
            CreateConfiguration(),
            new MemorySortPreferenceStore());
        viewModel.Configure(LearningDatasetViewMode.GeneralModeration);
        await viewModel.LoadAsync();
        var item = Assert.Single(viewModel.Items);

        item.Reason = "Erste Änderung";
        var firstSave = viewModel.SaveAsync(item);
        await dataset.FirstUpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        item.Reason = "Neueste Änderung";
        var coalescedSave = viewModel.SaveAsync(item);
        dataset.ReleaseFirstUpdate.TrySetResult();

        await Task.WhenAll(firstSave, coalescedSave);

        Assert.Collection(
            dataset.Drafts,
            first => Assert.Equal("Erste Änderung", first.Reason),
            second => Assert.Equal("Neueste Änderung", second.Reason));
        Assert.Equal("Neueste Änderung", item.Reason);
        Assert.Equal(2, item.RowVersion);
        Assert.False(item.HasEditableChanges);
        Assert.False(item.IsSaving);
    }

    [Fact]
    public async Task SaveAsync_FailedWrite_KeepsNewestDirtyValueVisible()
    {
        var dataset = new FailingDatasetService(Row(reason: "Ausgang", rowVersion: 0));
        using var viewModel = new LearningDatasetViewModel(
            dataset,
            CreateConfiguration(),
            new MemorySortPreferenceStore());
        viewModel.Configure(LearningDatasetViewMode.GeneralModeration);
        await viewModel.LoadAsync();
        var item = Assert.Single(viewModel.Items);
        item.Reason = "Noch nicht gespeichert";

        await viewModel.SaveAsync(item);

        Assert.Equal("Noch nicht gespeichert", item.Reason);
        Assert.True(item.HasEditableChanges);
        Assert.True(viewModel.HasError);
    }

    private static IConfigurationDataService CreateConfiguration()
    {
        var service = DispatchProxy.Create<
            IConfigurationDataService,
            ConfigurationDataProxy>();
        ((ConfigurationDataProxy)(object)service).Rules =
        [
            new PolicyRuleConfigurationRow(
                1,
                1,
                "General",
                ModerationRuleCodes.Spam,
                "Spam",
                "Wiederholter Spam.",
                "timeout",
                120),
        ];
        return service;
    }

    private static LearningDatasetRow Row(string reason, long rowVersion) =>
        new(
            new LearningSourceKey(LearningSourceKind.TwitchModeration, 11),
            "T-11",
            "spam spam",
            "spam spam",
            LearningDecision.Moderate,
            PolicySetCode.General,
            ModerationRuleCodes.Spam,
            ModerationActionKind.Timeout,
            120,
            reason,
            "Twitch-Moderation",
            new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero),
            rowVersion);

    public class ConfigurationDataProxy : DispatchProxy
    {
        public IReadOnlyList<PolicyRuleConfigurationRow> Rules { get; set; } = [];

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            if (targetMethod?.Name == nameof(
                    IConfigurationDataService.LoadPolicyRulesAsync))
            {
                return Task.FromResult(Rules);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class MemorySortPreferenceStore : ITableSortPreferenceStore
    {
        public bool TryGet(
            string scope,
            out TableSortPreference preference)
        {
            preference = null!;
            return false;
        }

        public TableSortPreference GetOrDefault(
            string scope,
            string defaultColumn,
            bool defaultDescending,
            IReadOnlySet<string>? validColumns = null) =>
            new(defaultColumn, defaultDescending);

        public void Save(string scope, string column, bool descending)
        {
        }
    }

    private sealed class BlockingDatasetService : ILearningDatasetService
    {
        private readonly LearningDatasetRow _initial;

        public BlockingDatasetService(LearningDatasetRow initial)
        {
            _initial = initial;
        }

        public TaskCompletionSource FirstUpdateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstUpdate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<LearningDatasetDraft> Drafts { get; } = [];

        public ValueTask<LearningDatasetPage> QueryAsync(
            LearningDatasetQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LearningDatasetPage([_initial], 1));

        public async ValueTask<LearningDatasetRow> UpdateAsync(
            LearningSourceKey source,
            long expectedRowVersion,
            LearningDatasetDraft draft,
            CancellationToken cancellationToken = default)
        {
            Drafts.Add(draft);
            if (Drafts.Count == 1)
            {
                FirstUpdateStarted.TrySetResult();
                await ReleaseFirstUpdate.Task.WaitAsync(cancellationToken);
            }

            return new LearningDatasetRow(
                source,
                source.DisplayId,
                draft.OriginalText,
                draft.OriginalText.ToLowerInvariant(),
                draft.Decision,
                draft.PolicySet,
                draft.RuleCode,
                draft.Action,
                draft.TimeoutSeconds,
                draft.Reason,
                _initial.SourceLabel,
                _initial.OccurredAtUtc,
                expectedRowVersion + 1);
        }

        public ValueTask RemoveAsync(
            LearningSourceKey source,
            long expectedRowVersion,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class FailingDatasetService : ILearningDatasetService
    {
        private readonly LearningDatasetRow _initial;

        public FailingDatasetService(LearningDatasetRow initial)
        {
            _initial = initial;
        }

        public ValueTask<LearningDatasetPage> QueryAsync(
            LearningDatasetQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LearningDatasetPage([_initial], 1));

        public ValueTask<LearningDatasetRow> UpdateAsync(
            LearningSourceKey source,
            long expectedRowVersion,
            LearningDatasetDraft draft,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<LearningDatasetRow>(
                new InvalidOperationException("Testfehler"));

        public ValueTask RemoveAsync(
            LearningSourceKey source,
            long expectedRowVersion,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}

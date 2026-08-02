using System.Reflection;
using Microsoft.UI.Xaml;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Pipeline;

namespace TwitchAI.App.Tests.Services;

public sealed class ModerationUiBridgeAiRunProjectionTests
{
    [Fact]
    public void ApplyAiRunSnapshot_ProjectsEveryActiveRunAndKeepsQueueFifo()
    {
        var state = new AppState();
        using var bridge = CreateBridge(state);
        var startedAt = new DateTimeOffset(
            2026,
            8,
            2,
            12,
            0,
            0,
            TimeSpan.Zero);

        InvokeApplyAiRunSnapshot(
            bridge,
            [
                CreateActivity("queued-2", LocalAiRunState.Queued, 4, startedAt.AddSeconds(4)),
                CreateActivity("active-2", LocalAiRunState.Running, 2, startedAt.AddSeconds(3)),
                CreateActivity("active-1", LocalAiRunState.Running, 1, startedAt.AddSeconds(2)),
                CreateActivity("queued-1", LocalAiRunState.Queued, 3, startedAt.AddSeconds(1)),
            ]);

        Assert.Equal(
            ["active-1", "active-2"],
            state.ActiveLocalAiRuns.Select(item => item.CorrelationId));
        Assert.Equal(
            ["queued-1", "queued-2"],
            state.QueuedLocalAiRuns.Select(item => item.CorrelationId));
        Assert.Equal(2, state.ActiveLocalAiRunCount);
        Assert.Equal(3, state.ActiveLocalAiMessageCount);
        Assert.Equal(2, state.QueuedLocalAiRunCount);
        Assert.Equal(7, state.QueuedLocalAiMessageCount);
        Assert.Equal(
            "AI arbeitet · 2 Läufe · 3 Nachrichten",
            state.LmStudioStatus);
    }

    [Fact]
    public void ApplyAiRunSnapshot_UpdatesRunsIndependentlyAndMovesStartedQueueItem()
    {
        var state = new AppState();
        using var bridge = CreateBridge(state);
        var startedAt = new DateTimeOffset(
            2026,
            8,
            2,
            12,
            0,
            0,
            TimeSpan.Zero);
        InvokeApplyAiRunSnapshot(
            bridge,
            [
                CreateActivity("queued-1", LocalAiRunState.Queued, 3, startedAt.AddSeconds(3)),
                CreateActivity("active-2", LocalAiRunState.Running, 2, startedAt.AddSeconds(2)),
                CreateActivity("active-1", LocalAiRunState.Running, 1, startedAt.AddSeconds(1)),
            ]);
        var firstActiveItem = state.ActiveLocalAiRuns[0];
        var secondActiveItem = state.ActiveLocalAiRuns[1];

        InvokeApplyAiRunSnapshot(
            bridge,
            [
                CreateActivity("queued-1", LocalAiRunState.Running, 3, startedAt.AddSeconds(5)),
                CreateActivity("active-2", LocalAiRunState.Running, 5, startedAt.AddSeconds(4)),
                CreateActivity("active-1", LocalAiRunState.Running, 1, startedAt.AddSeconds(1)),
            ]);

        Assert.Equal(
            ["active-1", "active-2", "queued-1"],
            state.ActiveLocalAiRuns.Select(item => item.CorrelationId));
        Assert.Empty(state.QueuedLocalAiRuns);
        Assert.Same(firstActiveItem, state.ActiveLocalAiRuns[0]);
        Assert.Same(secondActiveItem, state.ActiveLocalAiRuns[1]);
        Assert.Equal(1, state.ActiveLocalAiRuns[0].MessageCount);
        Assert.Equal(5, state.ActiveLocalAiRuns[1].MessageCount);
        Assert.Equal(3, state.ActiveLocalAiRuns[2].MessageCount);
    }

    [Fact]
    public void ShellFooter_ShowsRunStripInsteadOfSingleSummaryWhileRunsAreActive()
    {
        var state = new AppState
        {
            ActiveLocalAiRunCount = 2,
            QueuedLocalAiRunCount = 1,
        };
        var viewModel = new ShellViewModel(state, new StubCoordinator());

        Assert.Equal(Visibility.Collapsed, viewModel.LocalAiRunSummaryVisibility);
        Assert.Equal(Visibility.Visible, viewModel.LocalAiRunStripVisibility);

        state.ActiveLocalAiRunCount = 0;
        state.QueuedLocalAiRunCount = 0;

        Assert.Equal(Visibility.Visible, viewModel.LocalAiRunSummaryVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.LocalAiRunStripVisibility);
    }

    private static ModerationUiBridge CreateBridge(AppState state) =>
        new(
            state,
            new StubCoordinator(),
            new LocalAiRunActivityMonitor(),
            new InfrastructureOptions());

    private static LocalAiRunActivity CreateActivity(
        string correlationId,
        LocalAiRunState state,
        int messageCount,
        DateTimeOffset occurredAtUtc) =>
        new(
            correlationId,
            state,
            messageCount,
            "test-model",
            PromptTokens: null,
            CompletionTokens: null,
            TotalTokens: null,
            ElapsedMilliseconds: null,
            occurredAtUtc);

    private static void InvokeApplyAiRunSnapshot(
        ModerationUiBridge bridge,
        LocalAiRunActivity[] activities)
    {
        var method = typeof(ModerationUiBridge).GetMethod(
            "ApplyAiRunSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(bridge, [activities]);
    }

    private sealed class StubCoordinator : IModerationCoordinator
    {
        public event EventHandler<ModerationPipelineEventArgs>? EventReceived
        {
            add { }
            remove { }
        }

        public bool IsRunning => false;

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReanalyzeCaseAsync(
            long caseId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

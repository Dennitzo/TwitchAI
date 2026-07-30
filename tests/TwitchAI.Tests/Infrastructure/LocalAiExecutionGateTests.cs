using TwitchAI.Infrastructure.AI;

namespace TwitchAI.Tests.Infrastructure;

public sealed class LocalAiExecutionGateTests
{
    [Fact]
    public async Task AcquireAsync_SerializesIndependentInferenceCallers()
    {
        using var gate = new LocalAiExecutionGate();
        await using var first = await gate.AcquireAsync();
        var secondEntered = false;
        var secondTask = Task.Run(async () =>
        {
            await using var second = await gate.AcquireAsync();
            secondEntered = true;
        });

        await Task.Delay(30);
        Assert.False(secondEntered);

        await first.DisposeAsync();
        await secondTask;
        Assert.True(secondEntered);
    }
}

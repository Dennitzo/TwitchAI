using TwitchAI.Core.Models;

namespace TwitchAI.Core.Contracts;

public interface ILearningDatasetService
{
    ValueTask<LearningDatasetPage> QueryAsync(
        LearningDatasetQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<LearningDatasetRow> UpdateAsync(
        LearningSourceKey source,
        long expectedRowVersion,
        LearningDatasetDraft draft,
        CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(
        LearningSourceKey source,
        long expectedRowVersion,
        CancellationToken cancellationToken = default);
}

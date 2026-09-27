using TomYum.Domain.Entities;

namespace TomYum.Domain.Repositories;

/// <summary>Filter for <see cref="IChatLogRepository.ListAsync"/>; unset fields don't filter.</summary>
/// <param name="UserId">Messages sent by or privately to this user.</param>
/// <param name="Target">One channel (<c>#osu</c>) or one private conversation partner's name.</param>
/// <param name="BeforeId">Paging: only messages older than this id.</param>
/// <param name="Limit">Maximum messages (capped at 500).</param>
public sealed record ChatLogQuery(int? UserId = null, string? Target = null, long? BeforeId = null, int Limit = 100);

/// <summary>The in-game chat log.</summary>
public interface IChatLogRepository
{
    Task AddRangeAsync(IReadOnlyCollection<ChatLogEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>Matching messages, newest first.</summary>
    Task<IReadOnlyList<ChatLogEntry>> ListAsync(ChatLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>Deletes messages sent before <paramref name="cutoff"/>; returns how many.</summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default);
}

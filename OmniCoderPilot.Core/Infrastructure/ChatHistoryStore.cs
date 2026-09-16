using Microsoft.EntityFrameworkCore;
using OmniCoderPilot.Application;
using OmniCoderPilot.Domain;

namespace OmniCoderPilot.Infrastructure;

/// <summary>
/// Persistent chat history stored in SQLite. Supports search, date grouping, and session management.
/// </summary>
public sealed class ChatHistoryStore(IDbContextFactory<AppDbContext> dbFactory) : IChatHistoryStore
{
    /// <summary>
    /// Get all conversations grouped by date for the sidebar.
    /// </summary>
    public async Task<IReadOnlyList<ConversationGroup>> GetGroupedConversationsAsync(Guid workspaceId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var today = now.Date;
        var yesterday = today.AddDays(-1);
        var weekAgo = today.AddDays(-7);

        var conversations = await db.Conversations
            .Where(c => c.WorkspaceId == workspaceId)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(200)
            .ToListAsync(ct);

        var groups = new List<ConversationGroup>();

        var todayItems = conversations.Where(c => c.UpdatedAt >= today).ToList();
        if (todayItems.Count > 0)
            groups.Add(new ConversationGroup("Today", todayItems));

        var yesterdayItems = conversations.Where(c => c.UpdatedAt >= yesterday && c.UpdatedAt < today).ToList();
        if (yesterdayItems.Count > 0)
            groups.Add(new ConversationGroup("Yesterday", yesterdayItems));

        var weekItems = conversations.Where(c => c.UpdatedAt >= weekAgo && c.UpdatedAt < yesterday).ToList();
        if (weekItems.Count > 0)
            groups.Add(new ConversationGroup("Previous 7 days", weekItems));

        var olderItems = conversations.Where(c => c.UpdatedAt < weekAgo).ToList();
        if (olderItems.Count > 0)
            groups.Add(new ConversationGroup("Older", olderItems));

        return groups;
    }

    /// <summary>
    /// Search conversations by title and content.
    /// </summary>
    public async Task<IReadOnlyList<ConversationSearchResult>> SearchAsync(
        Guid workspaceId, string query, int maxResults = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var results = new List<ConversationSearchResult>();

        // Search by title
        var titleMatches = await db.Conversations
            .Where(c => c.WorkspaceId == workspaceId &&
                       c.Title.Contains(query))
            .OrderByDescending(c => c.UpdatedAt)
            .Take(maxResults)
            .Select(c => new ConversationSearchResult(c.Id, c.Title, c.UpdatedAt, "title", c.Title))
            .ToListAsync(ct);

        results.AddRange(titleMatches);

        // Search by message content
        var contentMatches = await db.Messages
            .Where(m => m.Content.Contains(query) &&
                       m.Conversation!.WorkspaceId == workspaceId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(maxResults)
            .ToListAsync(ct);

        var contentResults = contentMatches
            .Select(m => new ConversationSearchResult(
                m.ConversationId,
                m.Conversation!.Title,
                m.CreatedAt,
                "content",
                m.Content.Length > 100 ? m.Content.Substring(0, 100) + "…" : m.Content))
            .ToList();

        results.AddRange(contentResults);

        return results
            .DistinctBy(r => r.ConversationId)
            .OrderByDescending(r => r.Timestamp)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Get the last N prompts from a conversation for arrow-key history.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRecentPromptsAsync(
        Guid conversationId, int count = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Messages
            .Where(m => m.ConversationId == conversationId && m.Role == ChatRole.User)
            .OrderByDescending(m => m.CreatedAt)
            .Take(count)
            .Select(m => m.Content)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Auto-generate a title from the first user message.
    /// </summary>
    public async Task<string> GenerateTitleAsync(Guid conversationId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var firstMessage = await db.Messages
            .Where(m => m.ConversationId == conversationId && m.Role == ChatRole.User)
            .OrderBy(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (firstMessage is null) return "New conversation";

        var title = firstMessage.Content.Length > 60
            ? firstMessage.Content[..57] + "…"
            : firstMessage.Content;

        return title;
    }

    /// <summary>
    /// Delete a conversation and all its messages.
    /// </summary>
    public async Task DeleteAsync(Guid conversationId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var messages = await db.Messages
            .Where(m => m.ConversationId == conversationId)
            .ToListAsync(ct);

        db.Messages.RemoveRange(messages);

        var conv = await db.Conversations.FindAsync([conversationId], ct);
        if (conv is not null)
            db.Conversations.Remove(conv);

        await db.SaveChangesAsync(ct);
    }
}

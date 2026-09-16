using Microsoft.EntityFrameworkCore;
using OmniCoderPilot.Application;
using OmniCoderPilot.Domain;

namespace OmniCoderPilot.Infrastructure;

public sealed class TodoService(AppDbContext db) : ITodoService
{
    public async Task<IReadOnlyList<TodoItem>> GetAsync(Guid conversationId, CancellationToken ct)
    {
        return await db.Todos
            .Where(x => x.ConversationId == conversationId)
            .OrderBy(x => x.Order)
            .ToListAsync(ct);
    }

    public async Task UpsertAsync(Guid conversationId, IReadOnlyList<(string Content, string Status, int Order)> items, CancellationToken ct)
    {
        var existing = await db.Todos
            .Where(x => x.ConversationId == conversationId)
            .ToListAsync(ct);
        db.Todos.RemoveRange(existing);

        for (var i = 0; i < items.Count; i++)
        {
            var (content, status, order) = items[i];
            db.Todos.Add(new TodoItem
            {
                ConversationId = conversationId,
                Content = content,
                Status = status,
                Order = order == 0 ? i : order
            });
        }

        await db.SaveChangesAsync(ct);
    }
}

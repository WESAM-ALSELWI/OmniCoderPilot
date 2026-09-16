using Microsoft.EntityFrameworkCore;
using OmniCoderPilot.Domain;

namespace OmniCoderPilot.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<FileIndexEntry> FileIndex => Set<FileIndexEntry>();
    public DbSet<MemoryItem> Memories => Set<MemoryItem>();
    public DbSet<AgentTaskRun> TaskRuns => Set<AgentTaskRun>();
    public DbSet<ChangeSet> ChangeSets => Set<ChangeSet>();
    public DbSet<FileChange> FileChanges => Set<FileChange>();
    public DbSet<TodoItem> Todos => Set<TodoItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Workspace>().HasIndex(x => x.RootPath).IsUnique();
        b.Entity<FileIndexEntry>().HasIndex(x => new { x.WorkspaceId, x.RelativePath }).IsUnique();
        b.Entity<ChatMessage>().Property(x => x.Role).HasConversion<string>();
        b.Entity<AgentTaskRun>().Property(x => x.Status).HasConversion<string>();
        b.Entity<ChangeSet>().Property(x => x.Status).HasConversion<string>();
        b.Entity<TodoItem>().HasIndex(x => x.ConversationId);
    }
}

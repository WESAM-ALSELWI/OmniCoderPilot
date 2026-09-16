namespace OmniCoderPilot.Domain;


public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string RootPath { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<Conversation> Conversations { get; set; } = [];
}

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public string Title { get; set; } = "New conversation";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<ChatMessage> Messages { get; set; } = [];
}

public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }
    public ChatRole Role { get; set; }
    public string Content { get; set; } = "";
    public string? MetadataJson { get; set; }
    public int TokenEstimate { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FileIndexEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public string RelativePath { get; set; } = "";
    public string Language { get; set; } = "";
    public string SymbolsJson { get; set; } = "[]";
    public string ContentHash { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTimeOffset IndexedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MemoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public string Kind { get; set; } = "conversation";
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AgentTaskRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid ConversationId { get; set; }
    public string Description { get; set; } = "";
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Queued;
    public int ProgressPercent { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ChangeSet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid ConversationId { get; set; }
    public ChangeStatus Status { get; set; } = ChangeStatus.Preview;
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<FileChange> Files { get; set; } = [];
}

public sealed class FileChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChangeSetId { get; set; }
    public ChangeSet? ChangeSet { get; set; }
    public string RelativePath { get; set; } = "";
    public string OriginalText { get; set; } = "";
    public string NewText { get; set; } = "";
    public string UnifiedDiff { get; set; } = "";
}

public sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Content { get; set; } = "";
    /// <summary>pending | in-progress | done | cancelled</summary>
    public string Status { get; set; } = "pending";
    public int Order { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

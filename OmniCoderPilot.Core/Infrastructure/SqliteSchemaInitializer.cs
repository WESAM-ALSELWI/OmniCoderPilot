using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace OmniCoderPilot.Infrastructure;

public static class SqliteSchemaInitializer
{
    public static async Task EnsureCompatibleSchemaAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        try
        {
            await EnsureColumnAsync(connection, "Conversations", "UpdatedAt", "TEXT NOT NULL DEFAULT '2026-06-09T00:00:00.0000000+00:00'", ct);
            await EnsureColumnAsync(connection, "Conversations", "Title", "TEXT NOT NULL DEFAULT 'New chat'", ct);
            await EnsureColumnAsync(connection, "Messages", "MetadataJson", "TEXT NULL", ct);
            await EnsureColumnAsync(connection, "Messages", "TokenEstimate", "INTEGER NOT NULL DEFAULT 1", ct);
            await EnsureColumnAsync(connection, "Memories", "Kind", "TEXT NOT NULL DEFAULT 'conversation'", ct);
            await EnsureColumnAsync(connection, "TaskRuns", "ProgressPercent", "INTEGER NOT NULL DEFAULT 0", ct);
            await EnsureColumnAsync(connection, "TaskRuns", "Error", "TEXT NULL", ct);
            await EnsureColumnAsync(connection, "TaskRuns", "CompletedAt", "TEXT NULL", ct);
            await EnsureColumnAsync(connection, "ChangeSets", "Description", "TEXT NOT NULL DEFAULT ''", ct);
            await EnsureTableAsync(connection, "Todos", @"
                Id TEXT NOT NULL PRIMARY KEY,
                ConversationId TEXT NOT NULL,
                Content TEXT NOT NULL DEFAULT '',
                Status TEXT NOT NULL DEFAULT 'pending',
                ""Order"" INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT '2026-01-01T00:00:00.0000000+00:00'
            ", ct);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task EnsureTableAsync(DbConnection connection, string table, string columnDefs, CancellationToken ct)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{table}'";
        var exists = await check.ExecuteScalarAsync(ct);
        if (exists is not null) return;
        await using var create = connection.CreateCommand();
        create.CommandText = $"CREATE TABLE IF NOT EXISTS {table} ({columnDefs})";
        await create.ExecuteNonQueryAsync(ct);
        await using var idx = connection.CreateCommand();
        idx.CommandText = $"CREATE INDEX IF NOT EXISTS IX_{table}_ConversationId ON {table}(ConversationId)";
        await idx.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureColumnAsync(DbConnection connection, string table, string column, string definition, CancellationToken ct)
    {
        if (await ColumnExistsAsync(connection, table, column, ct)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> ColumnExistsAsync(DbConnection connection, string table, string column, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

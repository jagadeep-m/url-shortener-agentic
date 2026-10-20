using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Orchestrator.Core;

public sealed class RunConcurrencyException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class SqliteRunStateStore
{
    public string RunsDirectory { get; }
    public string DatabasePath => Path.Combine(RunsDirectory, "orchestrator.db");

    public SqliteRunStateStore(string runsDirectory = "runs")
    {
        RunsDirectory = Path.GetFullPath(runsDirectory);
        Directory.CreateDirectory(RunsDirectory);
    }

    public FileStream AcquireRunLease(Guid runId)
    {
        var directory = Path.Combine(RunsDirectory, runId.ToString("D"));
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(Path.Combine(directory, ".execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new RunConcurrencyException($"Run '{runId}' is already executing or its lock cannot be acquired.", exception);
        }
    }

    public async Task SaveAsync(RunContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RunId == Guid.Empty) throw new ArgumentException("Run ID cannot be empty.", nameof(context));
        var snapshot = JsonSerializer.Serialize(context, RunJson.Options);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$id", context.RunId.ToString("D"));
        command.Parameters.AddWithValue("$json", snapshot);
        command.Parameters.AddWithValue("$revision", context.Revision);
        command.CommandText = context.Revision == 0
            ? "INSERT INTO RunSnapshots (RunId, Revision, Snapshot) VALUES ($id, 1, $json)"
            : "UPDATE RunSnapshots SET Revision = Revision + 1, Snapshot = $json WHERE RunId = $id AND Revision = $revision";
        try
        {
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new RunConcurrencyException($"Run '{context.RunId}' changed since it was loaded. Reload it before saving.");
            context.Revision++;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new RunConcurrencyException($"Run '{context.RunId}' already exists; refusing to overwrite it.", exception);
        }
    }

    public async Task<RunContext?> LoadAsync(Guid runId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Revision, Snapshot FROM RunSnapshots WHERE RunId = $id";
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        try
        {
            var context = JsonSerializer.Deserialize<RunContext>(reader.GetString(1), RunJson.Options)
                ?? throw new InvalidDataException($"Run '{runId}' has an empty snapshot.");
            if (context.RunId != runId) throw new InvalidDataException("Stored run ID does not match the requested run.");
            context.Revision = reader.GetInt64(0);
            return context;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException($"Run '{runId}' contains an invalid snapshot or artifact hash.", exception);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 30 }.ToString());
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS RunSnapshots (RunId TEXT PRIMARY KEY, Revision INTEGER NOT NULL, Snapshot TEXT NOT NULL)";
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
}

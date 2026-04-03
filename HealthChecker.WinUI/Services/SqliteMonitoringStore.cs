using Microsoft.Data.Sqlite;
using HealthChecker_WinUI.Models;

namespace HealthChecker_WinUI.Services;

public sealed class SqliteMonitoringStore
{
    private const string SettingsStartWithWindows = "start_with_windows";
    private const string LegacyStateFileName = "state.json";

    private readonly string _dataDirectory;
    private readonly string _databasePath;
    private readonly AppStateStore _legacyStateStore = new();
    private readonly SemaphoreSlim _dbGate = new(1, 1);

    public SqliteMonitoringStore()
    {
        _dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HealthChecker");

        _databasePath = Path.Combine(_dataDirectory, "healthchecker.db");
    }

    public string DatabasePathDisplay => @"%AppData%\HealthChecker\healthchecker.db";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_dataDirectory);

            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);

            await ApplyPragmasAsync(connection, cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await TryMigrateLegacyJsonAsync(connection, cancellationToken);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<bool> LoadStartWithWindowsAsync(CancellationToken cancellationToken = default)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key LIMIT 1;";
            command.Parameters.AddWithValue("$key", SettingsStartWithWindows);

            var value = await command.ExecuteScalarAsync(cancellationToken);
            return string.Equals(value?.ToString(), "1", StringComparison.Ordinal);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task SaveStartWithWindowsAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                                  INSERT INTO settings(key, value)
                                  VALUES($key, $value)
                                  ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                                  """;
            command.Parameters.AddWithValue("$key", SettingsStartWithWindows);
            command.Parameters.AddWithValue("$value", enabled ? "1" : "0");

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<List<MonitoredTargetState>> LoadTargetsAsync(
        TimeSpan preloadHistoryWindow,
        CancellationToken cancellationToken = default)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);

            var targets = new List<MonitoredTargetState>();

            await using (var targetCommand = connection.CreateCommand())
            {
                targetCommand.CommandText = """
                                            SELECT id, name, description, address, resolved_ip, last_checked_utc, last_online_utc
                                            FROM targets
                                            ORDER BY name COLLATE NOCASE;
                                            """;

                await using var reader = await targetCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    targets.Add(new MonitoredTargetState
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        Name = reader.GetString(1),
                        Description = reader.GetString(2),
                        Address = reader.GetString(3),
                        ResolvedIp = reader.IsDBNull(4) ? null : reader.GetString(4),
                        LastCheckedUtc = reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)),
                        LastOnlineUtc = reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                        Samples = []
                    });
                }
            }

            if (targets.Count == 0)
            {
                return targets;
            }

            var cutoffUnix = DateTimeOffset.UtcNow.Subtract(preloadHistoryWindow).ToUnixTimeSeconds();

            foreach (var target in targets)
            {
                await using var sampleCommand = connection.CreateCommand();
                sampleCommand.CommandText = """
                                            SELECT ts_utc, is_online, ping_ms
                                            FROM probes
                                            WHERE target_id = $targetId AND ts_utc >= $cutoff
                                            ORDER BY ts_utc;
                                            """;
                sampleCommand.Parameters.AddWithValue("$targetId", target.Id.ToString());
                sampleCommand.Parameters.AddWithValue("$cutoff", cutoffUnix);

                await using var sampleReader = await sampleCommand.ExecuteReaderAsync(cancellationToken);
                while (await sampleReader.ReadAsync(cancellationToken))
                {
                    target.Samples.Add(new HealthSample
                    {
                        Timestamp = DateTimeOffset.FromUnixTimeSeconds(sampleReader.GetInt64(0)),
                        IsOnline = sampleReader.GetInt64(1) == 1,
                        PingMs = sampleReader.IsDBNull(2) ? null : sampleReader.GetInt64(2)
                    });
                }
            }

            return targets;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task UpsertTargetAsync(MonitoredTargetState state, CancellationToken cancellationToken = default)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                                  INSERT INTO targets(id, name, description, address, resolved_ip, last_checked_utc, last_online_utc)
                                  VALUES($id, $name, $description, $address, $resolvedIp, $lastCheckedUtc, $lastOnlineUtc)
                                  ON CONFLICT(id) DO UPDATE SET
                                      name = excluded.name,
                                      description = excluded.description,
                                      address = excluded.address,
                                      resolved_ip = excluded.resolved_ip,
                                      last_checked_utc = excluded.last_checked_utc,
                                      last_online_utc = excluded.last_online_utc;
                                  """;
            command.Parameters.AddWithValue("$id", state.Id.ToString());
            command.Parameters.AddWithValue("$name", state.Name);
            command.Parameters.AddWithValue("$description", state.Description);
            command.Parameters.AddWithValue("$address", state.Address);
            command.Parameters.AddWithValue("$resolvedIp", (object?)state.ResolvedIp ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastCheckedUtc", ToDbTimestamp(state.LastCheckedUtc));
            command.Parameters.AddWithValue("$lastOnlineUtc", ToDbTimestamp(state.LastOnlineUtc));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task RemoveTargetAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM targets WHERE id = $id;";
            command.Parameters.AddWithValue("$id", targetId.ToString());

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task AppendProbeAsync(Guid targetId, ProbeResult result, CancellationToken cancellationToken = default)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();

            try
            {
                await using (var insertProbe = connection.CreateCommand())
                {
                    insertProbe.Transaction = transaction;
                    insertProbe.CommandText = """
                                              INSERT INTO probes(target_id, ts_utc, is_online, ping_ms)
                                              VALUES($targetId, $timestamp, $isOnline, $pingMs);
                                              """;
                    insertProbe.Parameters.AddWithValue("$targetId", targetId.ToString());
                    insertProbe.Parameters.AddWithValue("$timestamp", result.Timestamp.ToUnixTimeSeconds());
                    insertProbe.Parameters.AddWithValue("$isOnline", result.IsOnline ? 1 : 0);
                    insertProbe.Parameters.AddWithValue("$pingMs", result.PingMs.HasValue ? result.PingMs.Value : DBNull.Value);

                    await insertProbe.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var updateTarget = connection.CreateCommand())
                {
                    updateTarget.Transaction = transaction;
                    updateTarget.CommandText = """
                                               UPDATE targets
                                               SET
                                                   resolved_ip = COALESCE($resolvedIp, resolved_ip),
                                                   last_checked_utc = $lastCheckedUtc,
                                                   last_online_utc = CASE WHEN $isOnline = 1 THEN $lastCheckedUtc ELSE last_online_utc END
                                               WHERE id = $targetId;
                                               """;
                    updateTarget.Parameters.AddWithValue("$targetId", targetId.ToString());
                    updateTarget.Parameters.AddWithValue("$resolvedIp", (object?)result.ResolvedIp ?? DBNull.Value);
                    updateTarget.Parameters.AddWithValue("$lastCheckedUtc", result.Timestamp.ToUnixTimeSeconds());
                    updateTarget.Parameters.AddWithValue("$isOnline", result.IsOnline ? 1 : 0);

                    await updateTarget.ExecuteNonQueryAsync(cancellationToken);
                }

                transaction.Commit();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                _ = exception;
            }
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<IReadOnlyList<HistoryBucket>> LoadHistoryBucketsAsync(
        Guid targetId,
        int maxBuckets,
        CancellationToken cancellationToken = default)
    {
        if (maxBuckets <= 0)
        {
            return [];
        }

        return await LoadHistoryBucketsCoreAsync(
            "WHERE target_id = $targetId",
            parameters => parameters.AddWithValue("$targetId", targetId.ToString()),
            maxBuckets,
            cancellationToken);
    }

    public async Task<IReadOnlyList<HistoryBucket>> LoadGlobalHistoryBucketsAsync(
        int maxBuckets,
        CancellationToken cancellationToken = default)
    {
        if (maxBuckets <= 0)
        {
            return [];
        }

        return await LoadHistoryBucketsCoreAsync(
            whereClause: string.Empty,
            parameters: null,
            maxBuckets,
            cancellationToken);
    }

    private async Task<IReadOnlyList<HistoryBucket>> LoadHistoryBucketsCoreAsync(
        string whereClause,
        Action<SqliteParameterCollection>? parameters,
        int maxBuckets,
        CancellationToken cancellationToken)
    {
        await _dbGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);

            long? minTs;
            long? maxTs;

            await using (var rangeCommand = connection.CreateCommand())
            {
                rangeCommand.CommandText = $"SELECT MIN(ts_utc), MAX(ts_utc) FROM probes {whereClause};";
                parameters?.Invoke(rangeCommand.Parameters);

                await using var rangeReader = await rangeCommand.ExecuteReaderAsync(cancellationToken);
                if (!await rangeReader.ReadAsync(cancellationToken))
                {
                    return [];
                }

                minTs = rangeReader.IsDBNull(0) ? null : rangeReader.GetInt64(0);
                maxTs = rangeReader.IsDBNull(1) ? null : rangeReader.GetInt64(1);
            }

            if (!minTs.HasValue || !maxTs.HasValue)
            {
                return [];
            }

            var spanSeconds = Math.Max(1, maxTs.Value - minTs.Value + 1);
            var bucketWidthSeconds = Math.Max(1, (long)Math.Ceiling(spanSeconds / (double)maxBuckets));

            var buckets = new List<HistoryBucket>(maxBuckets);

            await using var bucketCommand = connection.CreateCommand();
            bucketCommand.CommandText = $"""
                                        SELECT
                                            ((ts_utc - $minTs) / $bucketWidth) AS bucket_index,
                                            MIN(ts_utc) AS bucket_start,
                                            MAX(ts_utc) AS bucket_end,
                                            COUNT(*) AS sample_count,
                                            SUM(is_online) AS online_count,
                                            AVG(ping_ms) AS avg_ping
                                        FROM probes
                                        {whereClause}
                                        GROUP BY bucket_index
                                        ORDER BY bucket_index;
                                        """;
            bucketCommand.Parameters.AddWithValue("$minTs", minTs.Value);
            bucketCommand.Parameters.AddWithValue("$bucketWidth", bucketWidthSeconds);
            parameters?.Invoke(bucketCommand.Parameters);

            await using var bucketReader = await bucketCommand.ExecuteReaderAsync(cancellationToken);
            while (await bucketReader.ReadAsync(cancellationToken))
            {
                buckets.Add(new HistoryBucket
                {
                    StartUtc = DateTimeOffset.FromUnixTimeSeconds(bucketReader.GetInt64(1)),
                    EndUtc = DateTimeOffset.FromUnixTimeSeconds(bucketReader.GetInt64(2)),
                    Samples = bucketReader.GetInt32(3),
                    OnlineSamples = bucketReader.GetInt32(4),
                    AveragePingMs = bucketReader.IsDBNull(5) ? null : Convert.ToInt64(Math.Round(bucketReader.GetDouble(5)))
                });
            }

            return buckets;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              PRAGMA foreign_keys = ON;
                              PRAGMA journal_mode = WAL;
                              PRAGMA synchronous = NORMAL;
                              PRAGMA temp_store = MEMORY;
                              PRAGMA cache_size = -20000;
                              """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE IF NOT EXISTS settings (
                                  key TEXT PRIMARY KEY,
                                  value TEXT NOT NULL
                              );

                              CREATE TABLE IF NOT EXISTS targets (
                                  id TEXT PRIMARY KEY,
                                  name TEXT NOT NULL,
                                  description TEXT NOT NULL,
                                  address TEXT NOT NULL COLLATE NOCASE UNIQUE,
                                  resolved_ip TEXT NULL,
                                  last_checked_utc INTEGER NULL,
                                  last_online_utc INTEGER NULL
                              );

                              CREATE TABLE IF NOT EXISTS probes (
                                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                                  target_id TEXT NOT NULL,
                                  ts_utc INTEGER NOT NULL,
                                  is_online INTEGER NOT NULL,
                                  ping_ms INTEGER NULL,
                                  FOREIGN KEY(target_id) REFERENCES targets(id) ON DELETE CASCADE
                              );

                              CREATE INDEX IF NOT EXISTS idx_probes_target_ts ON probes(target_id, ts_utc);
                              """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task TryMigrateLegacyJsonAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM targets;";
        var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        if (count > 0)
        {
            return;
        }

        var legacyPath = Path.Combine(_dataDirectory, LegacyStateFileName);
        if (!File.Exists(legacyPath))
        {
            return;
        }

        var legacyState = await _legacyStateStore.LoadAsync(cancellationToken);
        if (legacyState.Targets.Count == 0 && !legacyState.StartWithWindows)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();

        foreach (var target in legacyState.Targets)
        {
            var id = target.Id == Guid.Empty ? Guid.NewGuid() : target.Id;

            await using (var insertTarget = connection.CreateCommand())
            {
                insertTarget.Transaction = transaction;
                insertTarget.CommandText = """
                                           INSERT INTO targets(id, name, description, address, resolved_ip, last_checked_utc, last_online_utc)
                                           VALUES($id, $name, $description, $address, $resolvedIp, $lastCheckedUtc, $lastOnlineUtc);
                                           """;
                insertTarget.Parameters.AddWithValue("$id", id.ToString());
                insertTarget.Parameters.AddWithValue("$name", target.Name);
                insertTarget.Parameters.AddWithValue("$description", target.Description);
                insertTarget.Parameters.AddWithValue("$address", target.Address);
                insertTarget.Parameters.AddWithValue("$resolvedIp", (object?)target.ResolvedIp ?? DBNull.Value);
                insertTarget.Parameters.AddWithValue("$lastCheckedUtc", ToDbTimestamp(target.LastCheckedUtc));
                insertTarget.Parameters.AddWithValue("$lastOnlineUtc", ToDbTimestamp(target.LastOnlineUtc));
                await insertTarget.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var sample in target.Samples.OrderBy(static sample => sample.Timestamp))
            {
                await using var insertSample = connection.CreateCommand();
                insertSample.Transaction = transaction;
                insertSample.CommandText = """
                                           INSERT INTO probes(target_id, ts_utc, is_online, ping_ms)
                                           VALUES($targetId, $tsUtc, $isOnline, $pingMs);
                                           """;
                insertSample.Parameters.AddWithValue("$targetId", id.ToString());
                insertSample.Parameters.AddWithValue("$tsUtc", sample.Timestamp.ToUnixTimeSeconds());
                insertSample.Parameters.AddWithValue("$isOnline", sample.IsOnline ? 1 : 0);
                insertSample.Parameters.AddWithValue("$pingMs", sample.PingMs.HasValue ? sample.PingMs.Value : DBNull.Value);
                await insertSample.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var settingsCommand = connection.CreateCommand())
        {
            settingsCommand.Transaction = transaction;
            settingsCommand.CommandText = """
                                          INSERT INTO settings(key, value)
                                          VALUES($key, $value)
                                          ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                                          """;
            settingsCommand.Parameters.AddWithValue("$key", SettingsStartWithWindows);
            settingsCommand.Parameters.AddWithValue("$value", legacyState.StartWithWindows ? "1" : "0");
            await settingsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();

        try
        {
            var backupName = $"state.migrated.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json";
            var backupPath = Path.Combine(_dataDirectory, backupName);
            File.Move(legacyPath, backupPath, overwrite: true);
        }
        catch
        {
        }
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        return new SqliteConnection(builder.ToString());
    }

    private static object ToDbTimestamp(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.ToUnixTimeSeconds() : DBNull.Value;
    }
}

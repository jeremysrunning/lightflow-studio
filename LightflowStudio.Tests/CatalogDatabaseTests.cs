using LightflowStudio;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LightflowStudio.Tests;

public sealed class CatalogDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lightflow-catalog-{Guid.NewGuid():N}");

    [Fact]
    public async Task ApprovedCreationAndReopen_PreserveIdentitySchemaAndData()
    {
        var service = CreateService();
        var created = await service.CreateNewAsync();

        Assert.Equal(CatalogOpenStatus.Created, created.Status);
        Assert.True(created.IsSuccess);
        Assert.Equal(CatalogDatabaseService.ApplicationIdentity, created.Session!.Identity.ApplicationIdentity);
        Assert.Equal(service.CurrentSchemaVersion, created.SchemaVersion);
        var catalogId = created.Session.Identity.CatalogId;
        InsertRoot(created.Session, "Archive");
        await created.Session.DisposeAsync();

        var reopened = await service.OpenExistingAsync();
        Assert.Equal(CatalogOpenStatus.Ready, reopened.Status);
        Assert.Equal(catalogId, reopened.Session!.Identity.CatalogId);
        Assert.Equal(1L, Scalar(reopened.Session, "SELECT count(*) FROM MediaRoots;"));
        await reopened.Session.DisposeAsync();
    }

    [Fact]
    public async Task OpenWithoutInitializationAuthority_NeverCreatesCatalog()
    {
        var locations = CreateLocations();
        var service = new CatalogDatabaseService(locations);

        var unavailable = await service.OpenExistingAsync();
        Assert.Equal(CatalogOpenStatus.StorageUnavailable, unavailable.Status);
        Assert.False(File.Exists(locations.CatalogDatabasePath));

        Directory.CreateDirectory(locations.CatalogDirectory);
        var missing = await service.OpenExistingAsync();
        Assert.Equal(CatalogOpenStatus.MissingExpectedCatalog, missing.Status);
        Assert.False(File.Exists(locations.CatalogDatabasePath));
    }

    [Fact]
    public async Task ApprovedCreation_DoesNotReplaceExistingCatalog()
    {
        var service = CreateService();
        var created = await service.CreateNewAsync();
        var catalogId = created.Session!.Identity.CatalogId;
        await created.Session.DisposeAsync();

        var duplicate = await service.CreateNewAsync();

        Assert.Equal(CatalogOpenStatus.AlreadyExists, duplicate.Status);
        var reopened = await service.OpenExistingAsync();
        Assert.Equal(catalogId, reopened.Session!.Identity.CatalogId);
        await reopened.Session.DisposeAsync();
    }

    [Fact]
    public async Task InitialMigration_HasOrderedHistoryAndExpectedApplicationIdentity()
    {
        var result = await CreateService().CreateNewAsync();
        var lastVersion = CatalogMigrations.All[^1].Version;
        Assert.Equal((long)lastVersion, Scalar(result.Session!, "PRAGMA user_version;"));
        Assert.Equal(CatalogDatabaseService.SqliteApplicationId,
            Convert.ToInt32(Scalar(result.Session!, "PRAGMA application_id;")));
        var expectedHistory = string.Join(",",
            CatalogMigrations.All.Select(migration => $"{migration.Version}:{migration.Name}"));
        Assert.Equal(expectedHistory,
            Convert.ToString(Scalar(result.Session!,
                "SELECT group_concat(Version || ':' || Name, ',') FROM (SELECT Version, Name FROM SchemaMigrations ORDER BY Version);")));
        await result.Session!.DisposeAsync();
    }

    [Fact]
    public async Task MediaRangeStore_PersistsPartialBoundariesAndClearsFullSourceIntent()
    {
        var result = await CreateService().CreateNewAsync();
        var session = result.Session!;
        var rootId = InsertRoot(session, "Archive");
        var assetId = InsertAsset(session, rootId, "clip.mp4", "clip.mp4");
        var store = new CatalogMediaRangeStore(() => session, () => new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero));

        var outOnly = new MediaRange(TimeSpan.FromSeconds(100), Out: TimeSpan.FromSeconds(70));
        await store.SaveAsync(assetId, outOnly);
        Assert.Equal(outOnly, await store.RestoreAsync(assetId));

        var inOnly = new MediaRange(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(20));
        await store.SaveAsync(assetId, inOnly);
        Assert.Equal(inOnly, await store.RestoreAsync(assetId));

        await store.SaveAsync(assetId, null);
        Assert.Null(await store.RestoreAsync(assetId));
        Assert.Equal(0L, Scalar(session, "SELECT count(*) FROM MediaAssetRanges;"));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task BrowserAssetStateStore_ProjectsSavedRangesForRequestedAssetsAndClearsThem()
    {
        var result = await CreateService().CreateNewAsync();
        var session = result.Session!;
        var rootId = InsertRoot(session, "Archive");
        var marked = InsertAsset(session, rootId, "marked.mp4", "marked.mp4");
        var unmarked = InsertAsset(session, rootId, "unmarked.mp4", "unmarked.mp4");
        var ranges = new CatalogMediaRangeStore(() => session);
        var states = new CatalogBrowserAssetStateStore(() => session);
        await ranges.SaveAsync(marked, new MediaRange(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));

        var projected = await states.GetAsync([marked, unmarked]);

        Assert.Equal(BrowserAssetState.ReviewRange, projected[marked]);
        Assert.Equal(BrowserAssetState.None, projected[unmarked]);

        await ranges.SaveAsync(marked, null);
        projected = await states.GetAsync([marked]);
        Assert.Equal(BrowserAssetState.None, projected[marked]);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task BrowserAssetStateStore_BatchesLargeRequestedSetsWithoutSqliteParameterOverflow()
    {
        var result = await CreateService().CreateNewAsync();
        var session = result.Session!;
        var store = new CatalogBrowserAssetStateStore(() => session);
        var requested = Enumerable.Range(0, 1200).Select(_ => Guid.NewGuid()).ToArray();

        var projected = await store.GetAsync(requested);

        Assert.Equal(requested.Length, projected.Count);
        Assert.All(projected.Values, state => Assert.Equal(BrowserAssetState.None, state));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ExistingVersionZeroCatalog_MigratesOnlyAfterBackupApproval()
    {
        var locations = CreateLocations();
        CreateVersionZeroCatalog(locations.CatalogDatabasePath);

        var blocked = await new CatalogDatabaseService(locations).OpenExistingAsync();
        Assert.Equal(CatalogOpenStatus.MigrationBackupRequired, blocked.Status);
        Assert.Equal(0, ReadUserVersion(locations.CatalogDatabasePath));

        var backup = new RecordingBackup();
        var migrated = await new CatalogDatabaseService(locations, backup).OpenExistingAsync();
        var lastVersion = CatalogMigrations.All[^1].Version;
        Assert.Equal(CatalogOpenStatus.Ready, migrated.Status);
        Assert.Equal([(0, lastVersion)], backup.Requests);
        Assert.Equal(lastVersion, migrated.SchemaVersion);
        await migrated.Session!.DisposeAsync();
    }

    [Fact]
    public async Task BackupFailure_PreventsMigration()
    {
        var locations = CreateLocations();
        CreateVersionZeroCatalog(locations.CatalogDatabasePath);
        var backup = new RecordingBackup(succeed: false);

        var result = await new CatalogDatabaseService(locations, backup).OpenExistingAsync();

        Assert.Equal(CatalogOpenStatus.MigrationBackupFailed, result.Status);
        Assert.Equal(0, ReadUserVersion(locations.CatalogDatabasePath));
    }

    [Fact]
    public async Task FailedMigration_RollsBackAndLeavesPriorSchemaRecoverable()
    {
        var locations = CreateLocations();
        var initial = await new CatalogDatabaseService(locations).CreateNewAsync();
        await initial.Session!.DisposeAsync();
        var lastRealVersion = CatalogMigrations.All[^1].Version;
        var migrations = CatalogMigrations.All.Concat(
        [
            new CatalogMigration(lastRealVersion + 1, "Deliberately failing test migration", (connection, transaction, _) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE MustRollBack (Id INTEGER PRIMARY KEY);";
                command.ExecuteNonQuery();
                throw new InvalidOperationException("test failure");
            })
        ]).ToArray();

        var failed = await new CatalogDatabaseService(locations, new RecordingBackup(), migrations)
            .OpenExistingAsync();

        Assert.Equal(CatalogOpenStatus.MigrationFailed, failed.Status);
        Assert.Equal(lastRealVersion, ReadUserVersion(locations.CatalogDatabasePath));
        Assert.False(TableExists(locations.CatalogDatabasePath, "MustRollBack"));
        var recovered = await new CatalogDatabaseService(locations).OpenExistingAsync();
        Assert.Equal(CatalogOpenStatus.Ready, recovered.Status);
        await recovered.Session!.DisposeAsync();
    }

    [Fact]
    public async Task MultipleMigrations_AreAppliedInVersionOrder()
    {
        // Starts from a raw version-zero file (rather than CreateNewAsync, which would apply the real
        // CatalogMigrations.All and leave the catalog already past version 1) so this fabricated,
        // deliberately-unordered migration set is what actually brings the schema from 0 to its top version.
        var locations = CreateLocations();
        CreateVersionZeroCatalog(locations.CatalogDatabasePath);
        var probeVersion = CatalogMigrations.All[^1].Version + 1;
        var version1 = CatalogMigrations.All[0];
        var probeCreate = new CatalogMigration(probeVersion, "Create ordering probe", (connection, transaction, _) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE MigrationOrderProbe (Value INTEGER NOT NULL);";
            command.ExecuteNonQuery();
        });
        var probeUse = new CatalogMigration(probeVersion + 1, "Use ordering probe", (connection, transaction, _) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO MigrationOrderProbe (Value) VALUES ($value);";
            command.Parameters.AddWithValue("$value", probeVersion + 1);
            command.ExecuteNonQuery();
        });
        var deliberatelyUnordered = CatalogMigrations.All.Skip(1)
            .Concat([probeUse, version1, probeCreate]).ToArray();

        var migrated = await new CatalogDatabaseService(
            locations, new RecordingBackup(), deliberatelyUnordered).OpenExistingAsync();

        Assert.Equal(CatalogOpenStatus.Ready, migrated.Status);
        Assert.Equal(probeVersion + 1, migrated.SchemaVersion);
        Assert.Equal((long)(probeVersion + 1), Scalar(migrated.Session!, "SELECT Value FROM MigrationOrderProbe;"));
        var expectedOrder = string.Join(",", deliberatelyUnordered.Select(migration => migration.Version).Order());
        Assert.Equal(expectedOrder, Convert.ToString(Scalar(migrated.Session!,
            "SELECT group_concat(Version, ',') FROM (SELECT Version FROM SchemaMigrations ORDER BY Version);")));
        await migrated.Session!.DisposeAsync();
    }

    [Fact]
    public async Task UnsupportedFutureSchema_FailsWithoutChangingCatalog()
    {
        var locations = CreateLocations();
        var created = await new CatalogDatabaseService(locations).CreateNewAsync();
        await created.Session!.DisposeAsync();
        SetUserVersion(locations.CatalogDatabasePath, 99);

        var result = await new CatalogDatabaseService(locations).OpenExistingAsync();

        Assert.Equal(CatalogOpenStatus.UnsupportedFutureSchema, result.Status);
        Assert.Equal(99, result.SchemaVersion);
        Assert.Equal(99, ReadUserVersion(locations.CatalogDatabasePath));
    }

    [Fact]
    public async Task ConnectionPolicy_IsAppliedAndVerifiedForEveryConnection()
    {
        var service = CreateService();
        var created = await service.CreateNewAsync();
        await created.Session!.DisposeAsync();
        var result = await service.OpenExistingAsync();

        Assert.True(result.Session!.RuntimePolicy.ForeignKeysEnabled);
        Assert.Equal("wal", result.Session.RuntimePolicy.JournalMode);
        Assert.Equal(CatalogSqliteConnectionFactory.FullSynchronousLevel,
            result.Session.RuntimePolicy.SynchronousLevel);
        Assert.Equal(CatalogSqliteConnectionFactory.BusyTimeoutMilliseconds,
            result.Session.RuntimePolicy.BusyTimeoutMilliseconds);
        using (var connection = OpenCatalog(result.Session))
        {
            Assert.Equal(1L, Scalar(connection, "PRAGMA foreign_keys;"));
            Assert.Equal(5_000L, Scalar(connection, "PRAGMA busy_timeout;"));
        }
        Assert.Throws<SqliteException>(() =>
            InsertAsset(result.Session, Guid.NewGuid(), "fresh-connection.mp4", "FRESH-CONNECTION.MP4"));
        await result.Session.DisposeAsync();
    }

    [Fact]
    public async Task Schema_EnforcesForeignKeysUniquenessStatusesAndRelativePaths()
    {
        var result = await CreateService().CreateNewAsync();
        var session = result.Session!;
        var rootId = InsertRoot(session, "Archive");
        InsertAsset(session, rootId, "2026/event/clip.mp4", "2026/EVENT/CLIP.MP4");

        Assert.Throws<SqliteException>(() =>
            InsertAsset(session, Guid.NewGuid(), "orphan.mp4", "ORPHAN.MP4"));
        Assert.Throws<SqliteException>(() =>
            InsertAsset(session, rootId, "2026/event/duplicate.mp4", "2026/EVENT/CLIP.MP4"));
        Assert.Throws<SqliteException>(() =>
            InsertAsset(session, rootId, @"2026\event\invalid.mp4", "INVALID"));
        Assert.Throws<SqliteException>(() => Execute(session,
            "UPDATE MediaRoots SET SourceStatus = 'deleted' WHERE RootId = $id;", ("$id", rootId.ToString("D"))));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitTransactionRollback_DoesNotPersistPartialWork()
    {
        var result = await CreateService().CreateNewAsync();
        using (var connection = OpenCatalog(result.Session!))
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = RootInsertSql;
            AddRootParameters(command, Guid.NewGuid(), "Rolled back");
            command.ExecuteNonQuery();
            transaction.Rollback();
        }

        Assert.Equal(0L, Scalar(result.Session!, "SELECT count(*) FROM MediaRoots;"));
        await result.Session!.DisposeAsync();
    }

    [Fact]
    public async Task NonSqliteCorruptAndForeignDatabases_AreClassifiedAndPreserved()
    {
        var nonSqliteLocations = CreateLocations(Path.Combine(_root, "not-sqlite"));
        Directory.CreateDirectory(nonSqliteLocations.CatalogDirectory);
        var nonSqliteBytes = "this is not sqlite"u8.ToArray();
        File.WriteAllBytes(nonSqliteLocations.CatalogDatabasePath, nonSqliteBytes);

        var nonSqlite = await new CatalogDatabaseService(nonSqliteLocations).OpenExistingAsync();
        Assert.Equal(CatalogOpenStatus.Unreadable, nonSqlite.Status);
        Assert.Equal(nonSqliteBytes, File.ReadAllBytes(nonSqliteLocations.CatalogDatabasePath));

        var corruptLocations = CreateLocations(Path.Combine(_root, "corrupt"));
        var created = await new CatalogDatabaseService(corruptLocations).CreateNewAsync();
        await created.Session!.DisposeAsync();
        using (var connection = OpenRaw(corruptLocations.CatalogDatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA writable_schema = ON;
                UPDATE sqlite_schema SET rootpage = 2147483647 WHERE name = 'MediaRoots';
                PRAGMA writable_schema = OFF;
                """;
            command.ExecuteNonQuery();
        }
        var corrupt = await new CatalogDatabaseService(corruptLocations).OpenExistingAsync();
        Assert.Equal(CatalogOpenStatus.Corrupt, corrupt.Status);
        Assert.True(File.Exists(corruptLocations.CatalogDatabasePath));

        var foreignLocations = CreateLocations(Path.Combine(_root, "foreign"));
        Directory.CreateDirectory(foreignLocations.CatalogDirectory);
        using (var connection = OpenRaw(foreignLocations.CatalogDatabasePath, create: true))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ForeignData (Id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }
        var foreign = await new CatalogDatabaseService(foreignLocations).OpenExistingAsync();
        Assert.Equal(CatalogOpenStatus.NotLightflowCatalog, foreign.Status);
        Assert.True(TableExists(foreignLocations.CatalogDatabasePath, "ForeignData"));
    }

    [Fact]
    public async Task ConflictingLightflowHeader_IsRejectedBeforeJournalModeMutation()
    {
        var locations = CreateLocations();
        Directory.CreateDirectory(locations.CatalogDirectory);
        using (var connection = OpenRaw(locations.CatalogDatabasePath, create: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                PRAGMA application_id = {CatalogDatabaseService.SqliteApplicationId};
                PRAGMA user_version = 1;
                CREATE TABLE ForeignData (Id INTEGER PRIMARY KEY);
                """;
            command.ExecuteNonQuery();
            Assert.Equal("delete", Convert.ToString(Scalar(connection, "PRAGMA journal_mode;")));
        }

        var result = await new CatalogDatabaseService(locations).OpenExistingAsync();

        Assert.Equal(CatalogOpenStatus.NotLightflowCatalog, result.Status);
        using var reopened = OpenRaw(locations.CatalogDatabasePath);
        Assert.Equal("delete", Convert.ToString(Scalar(reopened, "PRAGMA journal_mode;")));
        Assert.True(TableExists(locations.CatalogDatabasePath, "ForeignData"));
    }

    [Fact]
    public async Task UnreadablePathAndAccessDenied_AreClassifiedWithoutCreatingCatalog()
    {
        Assert.Equal(CatalogOpenStatus.Unreadable,
            CatalogDatabaseService.ClassifySqliteFailure(new UnauthorizedAccessException("denied")));

        var locations = CreateLocations();
        Directory.CreateDirectory(locations.CatalogDatabasePath);
        var result = await new CatalogDatabaseService(locations).OpenExistingAsync();
        Assert.Equal(CatalogOpenStatus.Unreadable, result.Status);
        Assert.True(Directory.Exists(locations.CatalogDatabasePath));
    }

    [Fact]
    public async Task CustomLocationAndPoolClose_AllowSameDirectoryRelocation()
    {
        var customCatalog = Path.Combine(_root, "custom", "precious");
        var locations = CreateLocations(_root, customCatalog);
        var result = await new CatalogDatabaseService(locations).CreateNewAsync();
        Assert.Equal(Path.Combine(customCatalog, LightflowStorageLocations.CatalogFileName),
            result.Session!.DatabasePath);
        await result.Session.DisposeAsync();

        var moved = Path.Combine(customCatalog, "relocated.db");
        File.Move(locations.CatalogDatabasePath, moved);
        File.Move(moved, locations.CatalogDatabasePath);
        var reopened = await new CatalogDatabaseService(locations).OpenExistingAsync();
        Assert.Equal(CatalogOpenStatus.Ready, reopened.Status);
        await reopened.Session!.DisposeAsync();
    }

    private CatalogDatabaseService CreateService() => new(CreateLocations());

    private LightflowStorageLocations CreateLocations(string? localData = null, string? catalog = null) =>
        LightflowStorageLocations.Create(localData ?? _root,
            catalog is null ? null : new LightflowStorageLocationOptions(catalog));

    private static void CreateVersionZeroCatalog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = OpenRaw(path, create: true);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA application_id = {CatalogDatabaseService.SqliteApplicationId}; PRAGMA user_version = 0;";
        command.ExecuteNonQuery();
    }

    private static int ReadUserVersion(string path)
    {
        using var connection = OpenRaw(path);
        return Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
    }

    private static void SetUserVersion(string path, int version)
    {
        using var connection = OpenRaw(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private static bool TableExists(string path, string table)
    {
        using var connection = OpenRaw(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static SqliteConnection OpenRaw(string path, bool create = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static Guid InsertRoot(CatalogDatabaseSession session, string name)
    {
        var id = Guid.NewGuid();
        using var connection = OpenCatalog(session);
        using var command = connection.CreateCommand();
        command.CommandText = RootInsertSql;
        AddRootParameters(command, id, name);
        command.ExecuteNonQuery();
        return id;
    }

    private static void AddRootParameters(SqliteCommand command, Guid id, string name)
    {
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
    }

    private static Guid InsertAsset(CatalogDatabaseSession session, Guid rootId, string relativePath, string key)
    {
        var assetId = Guid.NewGuid();
        using var connection = OpenCatalog(session);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MediaAssets
                (AssetId, RootId, RelativePath, RelativePathKey, MediaType, FileSizeBytes,
                 LastWriteUtcTicks, SourceStatus, CreatedUtc, UpdatedUtc)
            VALUES
                ($id, $root, $path, $key, 'video', 100, 638900000000000000,
                 'available', $now, $now);
            """;
        command.Parameters.AddWithValue("$id", assetId.ToString("D"));
        command.Parameters.AddWithValue("$root", rootId.ToString("D"));
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        return assetId;
    }

    private static void Execute(CatalogDatabaseSession session, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenCatalog(session);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(CatalogDatabaseSession session, string sql)
    {
        using var connection = OpenCatalog(session);
        return Scalar(connection, sql);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static SqliteConnection OpenCatalog(CatalogDatabaseSession session) =>
        new CatalogSqliteConnectionFactory(session.DatabasePath).OpenConnection();

    private const string RootInsertSql = """
        INSERT INTO MediaRoots (RootId, DisplayName, SourceStatus, CreatedUtc, UpdatedUtc)
        VALUES ($id, $name, 'online', $now, $now);
        """;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class RecordingBackup(bool succeed = true) : ICatalogMigrationBackup
    {
        public List<(int From, int To)> Requests { get; } = [];

        public Task<CatalogMigrationBackupResult> PrepareForMigrationAsync(
            string catalogDatabasePath,
            int currentSchemaVersion,
            int targetSchemaVersion,
            CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(catalogDatabasePath));
            Requests.Add((currentSchemaVersion, targetSchemaVersion));
            return Task.FromResult(succeed
                ? CatalogMigrationBackupResult.Success()
                : CatalogMigrationBackupResult.Failure("backup unavailable"));
        }
    }
}

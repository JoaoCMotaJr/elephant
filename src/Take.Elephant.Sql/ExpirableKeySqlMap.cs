﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Take.Elephant.Sql.Mapping;

[assembly: InternalsVisibleTo("Take.Elephant.Tests")]
namespace Take.Elephant.Sql
{
    /// <summary>
    /// Defines a <see cref="SqlMap{TKey,TValue}"/> that ignores expired rows.
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    public class ExpirableKeySqlMap<TKey, TValue> : SqlMap<TKey, TValue>, IExpirableKeyMap<TKey, TValue>
    {
        private readonly string _expirationColumnName;

        public ExpirableKeySqlMap(IDatabaseDriver databaseDriver, string connectionString, ITable table, IMapper<TKey> keyMapper, IMapper<TValue> valueMapper, string expirationColumnName)
            : base(new ExpirationDatabaseDriver(databaseDriver, expirationColumnName), connectionString, table, keyMapper, valueMapper)
        {
            _expirationColumnName = expirationColumnName ?? throw new ArgumentNullException(nameof(expirationColumnName));
            if (!Table.Columns.TryGetValue(expirationColumnName, out var expirationColumnType))
            {
                throw new ArgumentException($"The table doesn't contains an '{expirationColumnName}' column", nameof(expirationColumnName));
            }

            if (expirationColumnType.Type != DbType.Date
                && expirationColumnType.Type != DbType.DateTime
                && expirationColumnType.Type != DbType.DateTime2
                && expirationColumnType.Type != DbType.DateTimeOffset)
            {
                throw new ArgumentException($"The expiration column '{expirationColumnName}' must have a date type", nameof(expirationColumnName));
            }
        }

        public virtual Task<bool> SetRelativeKeyExpirationAsync(TKey key, TimeSpan ttl) =>
            SetAbsoluteKeyExpirationAsync(key, DateTimeOffset.UtcNow.Add(ttl));

        public virtual async Task<bool> SetAbsoluteKeyExpirationAsync(TKey key, DateTimeOffset expiration)
        {
            using var cancellationTokenSource = CreateCancellationTokenSource();
            await using var connection = await GetConnectionAsync(cancellationTokenSource.Token).ConfigureAwait(false);
            var keyColumnValues = KeyMapper.GetColumnValues(key);
            var columnValues = new Dictionary<string, object>
                {
                    {_expirationColumnName, expiration}
                };

            await using var command = connection.CreateTextCommand(
                DatabaseDriver.GetSqlStatementTemplate(SqlStatement.Update),
                new
                {
                    schemaName = DatabaseDriver.ParseIdentifier(Table.Schema ?? DatabaseDriver.DefaultSchema),
                    tableName = DatabaseDriver.ParseIdentifier(Table.Name),
                    columnValues = SqlHelper.GetCommaEqualsStatement(DatabaseDriver, columnValues.Keys.ToArray()),
                    filter = SqlHelper.GetAndEqualsStatement(DatabaseDriver, keyColumnValues.Keys.ToArray())                    
                },
                keyColumnValues.Concat(columnValues).Select(c => c.ToDbParameter(DatabaseDriver)));

            if (await command.ExecuteNonQueryAsync(cancellationTokenSource.Token).ConfigureAwait(false) == 0)
            {
                return false;
            }

            return true;
        }

        public virtual async Task<bool> RemoveExpirationAsync(TKey key)
        {
            using var cancellationTokenSource = CreateCancellationTokenSource();
            await using var connection = await GetConnectionAsync(cancellationTokenSource.Token).ConfigureAwait(false);
            var keyColumnValues = KeyMapper.GetColumnValues(key);
            var columnValues = new Dictionary<string, object>
                {
                    {_expirationColumnName, DBNull.Value}
                };

            await using var command = connection.CreateTextCommand(
                DatabaseDriver.GetSqlStatementTemplate(SqlStatement.Update),
                new
                {
                    schemaName = DatabaseDriver.ParseIdentifier(Table.Schema ?? DatabaseDriver.DefaultSchema),
                    tableName = DatabaseDriver.ParseIdentifier(Table.Name),
                    columnValues = SqlHelper.GetCommaEqualsStatement(DatabaseDriver, columnValues.Keys.ToArray()),
                    filter = SqlHelper.GetCombinedAndStatement(DatabaseDriver, 
                                                                                             SqlHelper.GetAndEqualsStatement(DatabaseDriver, keyColumnValues.Keys.ToArray()),
                                                                                             SqlHelper.GetIsNotNullStatement(DatabaseDriver, columnValues.Keys.ToArray()))
                },
                keyColumnValues.Concat(columnValues).Select(c => c.ToDbParameter(DatabaseDriver)));

            var teste = SqlHelper.GetCombinedAndStatement(DatabaseDriver,
                                                                                             SqlHelper.GetAndEqualsStatement(DatabaseDriver, keyColumnValues.Keys.ToArray()),
                                                                                             SqlHelper.GetIsNotNullStatement(DatabaseDriver, columnValues.Keys.ToArray()));

            if (await command.ExecuteNonQueryAsync(cancellationTokenSource.Token).ConfigureAwait(false) == 0)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Injects a SQL statement to avoid retrieving expired items.
        /// </summary>
        /// <seealso cref="Take.Elephant.Sql.IDatabaseDriver" />
        internal class ExpirationDatabaseDriver : IDatabaseDriver
        {
            private readonly IDatabaseDriver _underlyingDatabaseDriver;
            private readonly string _expirationColumnName;

            public ExpirationDatabaseDriver(IDatabaseDriver underlyingDatabaseDriver, string expirationColumnName)
            {
                _underlyingDatabaseDriver = underlyingDatabaseDriver ?? throw new ArgumentNullException(nameof(underlyingDatabaseDriver));
                _expirationColumnName = underlyingDatabaseDriver.ParseIdentifier(expirationColumnName ?? throw new ArgumentNullException(nameof(expirationColumnName)));
            }

            public DbConnection CreateConnection(string connectionString)
                => new ExpirationDbConnection(_underlyingDatabaseDriver.CreateConnection(connectionString));

            public string GetSqlStatementTemplate(SqlStatement sqlStatement)
            {
                var sql = _underlyingDatabaseDriver.GetSqlStatementTemplate(sqlStatement);
                switch (sqlStatement)
                {
                    case SqlStatement.Select:
                    case SqlStatement.SelectCount:
                    case SqlStatement.SelectTop1:
                    case SqlStatement.SelectSkipTake:
                    case SqlStatement.SelectDistinct:
                    case SqlStatement.SelectCountDistinct:
                    case SqlStatement.SelectDistinctSkipTake:
                        var expirationFilter = $"AND ({_expirationColumnName} IS NULL OR {_expirationColumnName} > @ExpirableKeySqlMap_ExpirationDate)";
                        sql = InjectSqlFilter(sql, expirationFilter);
                        break;
                }
                return sql;
            }

            public string GetSqlTypeName(DbType dbType) => _underlyingDatabaseDriver.GetSqlTypeName(dbType);

            public DbParameter CreateParameter(string parameterName, object value) => _underlyingDatabaseDriver.CreateParameter(parameterName, value);

            public DbParameter CreateParameter(string parameterName, object value, SqlType sqlType) => _underlyingDatabaseDriver.CreateParameter(parameterName, value, sqlType);

            public string ParseParameterName(string parameterName) => _underlyingDatabaseDriver.ParseParameterName(parameterName);

            public string ParseIdentifier(string identifier) => _underlyingDatabaseDriver.ParseIdentifier(identifier);

            public TimeSpan Timeout => _underlyingDatabaseDriver.Timeout;

            public string DefaultSchema => _underlyingDatabaseDriver.DefaultSchema;

            private static string InjectSqlFilter(string sql, string filter)
            {
                if (sql.Contains("ORDER BY")) return sql.Replace("ORDER BY", $"{filter} ORDER BY");
                return $"{sql} {filter}";
            }
        }
    }

    /// <summary>
    /// Implements a new behavior for DbConnection with a custom parameter.
    /// </summary>
    internal class ExpirationDbConnection : DbConnection
    {
        private readonly DbConnection _dbConnection;
        public ExpirationDbConnection(DbConnection dbConnection)
        {
            _dbConnection = dbConnection;
        }

        public override string ConnectionString { get => _dbConnection.ConnectionString; set => _dbConnection.ConnectionString = value; }

        public override string Database => _dbConnection.Database;

        public override string DataSource => _dbConnection.DataSource;

        public override string ServerVersion => _dbConnection.ServerVersion;

        public override ConnectionState State => _dbConnection.State;

        public override void ChangeDatabase(string databaseName)
        {
            _dbConnection.ChangeDatabase(databaseName);
        }

        public override void Close()
        {
            _dbConnection.Close();
        }

        public override void Open()
        {
            _dbConnection.Open();
        }

        protected override DbCommand CreateDbCommand()
        {
            var command = _dbConnection.CreateCommand();
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@ExpirableKeySqlMap_ExpirationDate";
            parameter.Value = DateTimeOffset.UtcNow;

            command.Parameters.Add(parameter);
            return command;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _dbConnection.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await _dbConnection.DisposeAsync();
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            return _dbConnection.BeginTransaction(isolationLevel);
        }
    }
}

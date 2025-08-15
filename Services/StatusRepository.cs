
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Minis.Services
{
    public interface IDbConnectionFactory
    {
        IDbConnection Create();
    }

    public sealed class SqlConnectionFactory : IDbConnectionFactory
    {
        private readonly string _cs;
        public SqlConnectionFactory(string connectionString) => _cs = connectionString;
        public IDbConnection Create() => new System.Data.SqlClient.SqlConnection(_cs);
    }

    public sealed class OrderRow
    {
        public int Id { get; set; }
        public string Shop { get; set; } = "";
        public DateTime LastModified { get; set; }
        public bool EmailSentForOnTheWay { get; set; }
        public bool EmailSentForDelivered { get; set; }
    }

    public sealed class StatusUpdate
    {
        public string DeliveryId { get; set; } = "";
        public int StatusCode { get; set; }
        public string? Latitude { get; set; }
        public string? Longitude { get; set; }
        public string? DriverName { get; set; }
        public string? DriverPhone { get; set; }
        public DateTimeOffset? PickupTimeUtc { get; set; }
    }

    public interface IStatusRepository
    {
        Task<(int orderId, string? fcmToken)?> ResolveOrderAndTokenAsync(string key, CancellationToken ct);
        Task<OrderRow?> GetOrderByDeliveryIdAsync(string deliveryId, CancellationToken ct);
        Task<int> UpdateOrderStatusAsync(StatusUpdate update, CancellationToken ct);

        // Lookups/updates by internal order id (useful for Swagger/manual tests):
        Task<OrderRow?> GetOrderByInternalIdAsync(int id, CancellationToken ct);
        Task<int> UpdateOrderStatusByIdAsync(int id, StatusUpdate update, CancellationToken ct);

        // NEW: resolve internal order id -> deliveryId (column or JSON)
        Task<string?> GetDeliveryIdByInternalIdAsync(int orderId, CancellationToken ct);
    }

    public sealed class StatusRepository : IStatusRepository
    {
        private readonly IDbConnectionFactory _factory;
        public StatusRepository(IDbConnectionFactory factory) => _factory = factory;

       

        // StatusRepository.cs (implement)
        public async Task<(int orderId, string? fcmToken)?> ResolveOrderAndTokenAsync(string key, CancellationToken ct)
        {
            const string sql = @"
SELECT TOP 1
  ID AS orderId,
  JSON_VALUE(Metadata, '$.push.fcmToken') AS fcmToken
FROM dbo.Orders
WHERE deliveryId = @key
   OR JSON_VALUE(Metadata,'$.delivery.id') = @key
   OR (TRY_CAST(@key AS INT) IS NOT NULL AND ID = TRY_CAST(@key AS INT));";

            using var conn = _factory.Create();
            var row = await conn.QuerySingleOrDefaultAsync<(int orderId, string? fcmToken)>(
                new CommandDefinition(sql, new { key }, cancellationToken: ct));
            return row.orderId == 0 ? null : row;
        }
        // ---- small helper to print which DB we're on (once per call) ----
        private static async Task DebugPrintDbAsync(IDbConnection conn)
        {
            try
            {
                var server = await conn.ExecuteScalarAsync<string>("SELECT CAST(@@SERVERNAME AS NVARCHAR(256))");
                var db = await conn.ExecuteScalarAsync<string>("SELECT DB_NAME()");
                Console.WriteLine($"[DB] server={server}, db={db}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] diag failed: {ex.Message}");
            }
        }

        public async Task<OrderRow?> GetOrderByDeliveryIdAsync(string deliveryId, CancellationToken ct)
        {
            const string sql = @"
SELECT ID AS Id, shop AS Shop, lastModified,
       ISNULL(EmailSentForOnTheWay,0)  AS EmailSentForOnTheWay,
       ISNULL(EmailSentForDelivered,0) AS EmailSentForDelivered
FROM dbo.Orders
WHERE deliveryId = @deliveryId
   OR JSON_VALUE(Metadata,'$.delivery.id') = @deliveryId;";

            using var conn = _factory.Create();
            await ((DbConnection)conn).OpenAsync(ct);
            await DebugPrintDbAsync(conn);

            Console.WriteLine($"[GetByDeliveryId] deliveryId={deliveryId}");

            return await conn.QuerySingleOrDefaultAsync<OrderRow>(
                new CommandDefinition(sql, new { deliveryId }, cancellationToken: ct));
        }

        public async Task<int> UpdateOrderStatusAsync(StatusUpdate u, CancellationToken ct)
        {
            const string sql = @"
UPDATE dbo.Orders
   SET Status = @StatusCode
 WHERE (JSON_VALUE(Metadata,'$.delivery.id') = @DeliveryId OR deliveryId = @DeliveryId)
   AND (Status IS NULL OR Status <> @StatusCode);

SELECT @@ROWCOUNT;";

            using var conn = _factory.Create();
            await ((System.Data.Common.DbConnection)conn).OpenAsync(ct);
            await DebugPrintDbAsync(conn);

            Console.WriteLine("[UpdateStatusOnly] deliveryId={0}, status={1}", u.DeliveryId, u.StatusCode);

            var rows = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                sql,
                new
                {
                    u.StatusCode,
                    u.DeliveryId
                },
                cancellationToken: ct));

            Console.WriteLine($"[UpdateStatusOnly] rowsAffected={rows}");
            return rows;
        }


        public async Task<OrderRow?> GetOrderByInternalIdAsync(int id, CancellationToken ct)
        {
            const string sql = @"
SELECT ID AS Id, shop AS Shop, lastModified,
       ISNULL(EmailSentForOnTheWay,0)  AS EmailSentForOnTheWay,
       ISNULL(EmailSentForDelivered,0) AS EmailSentForDelivered
FROM dbo.Orders
WHERE ID = @id;";

            using var conn = _factory.Create();
            await ((DbConnection)conn).OpenAsync(ct);
            await DebugPrintDbAsync(conn);

            Console.WriteLine($"[GetById] id={id}");

            return await conn.QuerySingleOrDefaultAsync<OrderRow>(
                new CommandDefinition(sql, new { id }, cancellationToken: ct));
        }

        public async Task<int> UpdateOrderStatusByIdAsync(int id, StatusUpdate u, CancellationToken ct)
        {
            const string sql = @"
UPDATE dbo.Orders
   SET Status = @StatusCode
 WHERE ID = @Id
   AND (Status IS NULL OR Status <> @StatusCode);

SELECT @@ROWCOUNT;";

            using var conn = _factory.Create();
            await ((DbConnection)conn).OpenAsync(ct);
            await DebugPrintDbAsync(conn);

            Console.WriteLine("[UpdateById] id={0}, status={1}", id, u.StatusCode);

            var rows = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                sql,
                new
                {
                    Id = id,
                    u.StatusCode
                },
                cancellationToken: ct));

            Console.WriteLine($"[UpdateById] rowsAffected={rows}");
            return rows;
        }
        public async Task<string?> GetDeliveryIdByInternalIdAsync(int orderId, CancellationToken ct)
        {
            const string sql = @"
SELECT COALESCE(deliveryId, JSON_VALUE(Metadata,'$.delivery.id'))
FROM dbo.Orders
WHERE ID = @orderId;";

            using var conn = _factory.Create();
            await ((DbConnection)conn).OpenAsync(ct);
            await DebugPrintDbAsync(conn);

            var result = await conn.ExecuteScalarAsync<string?>(
                new CommandDefinition(sql, new { orderId }, cancellationToken: ct));

            Console.WriteLine($"[ResolveDeliveryId] orderId={orderId} -> deliveryId={(result ?? "(null)")}");
            return result;
        }
    }
}

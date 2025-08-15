using Dapper;
using System.Data.SqlClient;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minis.Services
{

    namespace Minis.Services
    {
        public static class OrderService
        {
            private static string Cxn(IConfiguration cfg) =>
                 cfg.GetConnectionString("DefaultConnection")
                ?? cfg.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Missing connection string 'Default' (or 'DefaultConnection').");

            public static async Task UpdateTransferMetadataAsync(
           int orderId,
           string stripeChargeId,
           string? transferId,
           string? destinationPaymentId,
           decimal splitPercent,
           IConfiguration config)
            {
                var connStr = config.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");

                const string sel = "SELECT Metadata FROM Orders WHERE Id = @orderId";
                const string upd = "UPDATE Orders SET Metadata = @metadata WHERE Id = @orderId";

                await using var conn = new SqlConnection(connStr);
                var existing = await conn.QuerySingleOrDefaultAsync<string>(sel, new { orderId });

                JsonNode root;
                if (string.IsNullOrWhiteSpace(existing))
                {
                    root = new JsonObject();
                }
                else
                {
                    try { root = JsonNode.Parse(existing) ?? new JsonObject(); }
                    catch { root = new JsonObject(); }
                }

                var stripeNode = root["stripe"] as JsonObject ?? new JsonObject();
                root["stripe"] = stripeNode;

                stripeNode["chargeId"] = stripeChargeId;
                var transferNode = new JsonObject
                {
                    ["id"] = transferId is { Length: > 0 } ? transferId : null,
                    ["destinationPaymentId"] = destinationPaymentId is { Length: > 0 } ? destinationPaymentId : null,
                    ["splitPercent"] = splitPercent
                };
                stripeNode["transfer"] = transferNode;

                var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                await conn.ExecuteAsync(upd, new { orderId, metadata = serialized });
            }
            public static async Task UpdateDeliveryMetadataAsync(
      int orderId, string provider, string deliveryId, IConfiguration cfg)
            {
                const string sql = @"
UPDATE dbo.Orders
SET Metadata = JSON_MODIFY(
                 JSON_MODIFY(
                   COALESCE(NULLIF(Metadata, ''), N'{}'),
                   '$.delivery.provider', @provider
                 ),
                 '$.delivery.id', @deliveryId
               ),
    deliveryId = NULLIF(@deliveryId, '')
WHERE Id = @orderId;";

                await using var conn = new SqlConnection(Cxn(cfg));
                await conn.OpenAsync();
                var rows = await conn.ExecuteAsync(sql, new { orderId, provider, deliveryId });

                Console.WriteLine($"[DELIVERY SAVE] order={orderId}, provider={provider}, id={deliveryId}, rows={rows}");
            }

            public static async Task<int> UpsertCustomerAsync(string uuid, string? email, string? name, IConfiguration cfg)
            {
                const string sql = @"
IF EXISTS (SELECT 1 FROM Customers WHERE UUID = @UUID)
BEGIN
    UPDATE Customers SET Email = ISNULL(@Email, Email), Name = ISNULL(@Name, Name)
    WHERE UUID = @UUID;
    SELECT Id FROM Customers WHERE UUID = @UUID;
END
ELSE
BEGIN
    INSERT INTO Customers (UUID, Email, Name) VALUES (@UUID, @Email, @Name);
    SELECT CAST(SCOPE_IDENTITY() AS INT);
END";
                await using var conn = new SqlConnection(Cxn(cfg));
                return await conn.ExecuteScalarAsync<int>(sql, new { UUID = uuid, Email = email, Name = name });
            }

            public static async Task<int> InsertOrderAsync(
     int customerId,
     int miniAppId,
     decimal total,
     object metadata,
     IConfiguration cfg,
     string? uuid = null,
     string? fcmToken = null)
            {
                const string sql = """
DECLARE @newId INT;

INSERT INTO dbo.Orders (CustomerId, MiniAppId, Total, Metadata, Status, CreatedAt)
VALUES (@CustomerId, @MiniAppId, @Total, @Metadata, @Status, SYSUTCDATETIME());

SET @newId = CAST(SCOPE_IDENTITY() AS INT);

-- Ensure uuid and notifications.fcmToken exist in JSON metadata
UPDATE dbo.Orders
SET Metadata = JSON_MODIFY(
                 JSON_MODIFY(
                   COALESCE(NULLIF(Metadata, ''), N'{}'),
                   '$.uuid', @Uuid
                 ),
                 '$.notifications.fcmToken', @FcmToken
               )
WHERE Id = @newId;

SELECT @newId;
""";

                var json = System.Text.Json.JsonSerializer.Serialize(metadata);
                await using var conn = new SqlConnection(Cxn(cfg));
                return await conn.ExecuteScalarAsync<int>(sql, new
                {
                    CustomerId = customerId,
                    MiniAppId = miniAppId,
                    Total = total,
                    Metadata = json,
                    Status = 0,                              // adjust to your enum if needed
                    Uuid = (object?)uuid ?? DBNull.Value,    // will set null in JSON if missing
                    FcmToken = (object?)fcmToken ?? DBNull.Value
                });
            }
        }
    }

        }

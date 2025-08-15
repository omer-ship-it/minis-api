#nullable enable
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Minis.Delivery;
using Minis.Models;
using Minis.Services;
using Minis.Services.Minis.Services;
using Stripe;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeliveryJobResult = Minis.Delivery.DeliveryJobResult;
using SubmitOrderRequestDto = Minis.Delivery.SubmitOrderRequest;


var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IFcmPushService, FcmPushService>();
// Register FCM push service

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IApnsPushService, ApnsPushService>();
builder.Services.AddSingleton<IDbConnectionFactory>(sp =>
    new SqlConnectionFactory(builder.Configuration.GetConnectionString("Default")!));

// ✅ Repo + notifier used by /webhooks/delivery-status
builder.Services.AddScoped<IStatusRepository, StatusRepository>();
builder.Services.AddScoped<INotificationService, NotificationService>();

builder.Services.AddHttpClient<IEmailService, BrevoEmailService>(c => { c.Timeout = TimeSpan.FromSeconds(20); });
builder.Services.AddHttpClient<GophrProvider>();
builder.Services.AddHttpClient<OrkestroProvider>();
builder.Services.AddTransient<IDeliveryProvider>(sp => sp.GetRequiredService<GophrProvider>());
builder.Services.AddTransient<IDeliveryProvider>(sp => sp.GetRequiredService<OrkestroProvider>());
builder.Services.AddSingleton<IDeliveryRouter, DeliveryRouter>();
// DI for DB + repo
var cs = builder.Configuration.GetConnectionString("DefaultConnection")
         ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");
builder.Services.AddSingleton<IDbConnectionFactory>(_ => new SqlConnectionFactory(cs));
builder.Services.AddScoped<IStatusRepository, StatusRepository>();


builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"]
    ?? "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";

builder.Services.AddSingleton<IDbConnectionFactory>(_ => new SqlConnectionFactory(cs));

// enable real repo
builder.Services.AddScoped<IStatusRepository, StatusRepository>();
var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();



app.MapPost("/diag/push-apns", async (
    [FromBody] ApnsPushRequest req,
    IApnsPushService apns,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Token))
        return Results.BadRequest(new { error = "Token is required" });

    var (ok, status, body) = await apns.SendAsync(
        req.Token,
        req.Title ?? "Ping from APNs",
        req.Body ?? "Hello from your backend 👋",
        req.Data ?? new Dictionary<string, string>(),
        ct
    );

    return ok
        ? Results.Ok(new { ok, status })
        : Results.Problem(detail: body ?? status);
});

app.MapPost("/create-payment-intent", async (CreatePIRequest payload) =>
{
    if (payload.Amount <= 0 || string.IsNullOrWhiteSpace(payload.Currency) || string.IsNullOrWhiteSpace(payload.PaymentMethod))
        return Results.BadRequest(new { error = "amount/currency/paymentMethod required" });

    try
    {
        var service = new PaymentIntentService();
        var options = new PaymentIntentCreateOptions
        {
            Amount = payload.Amount,
            Currency = payload.Currency,
            PaymentMethod = payload.PaymentMethod,   // e.g. pm_card_visa or Apple Pay PM id
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
            {
                Enabled = true,
                AllowRedirects = "never"             // <— key line
            },
            // ConfirmationMethod = "automatic",     // <— remove this line
            Confirm = true,                          // create + confirm in one call
            CaptureMethod = "automatic",
            Description = payload.Description,
            Metadata = payload.Metadata
        };
        var pi = await service.CreateAsync(options);
        return Results.Json(new { clientSecret = pi.ClientSecret, status = pi.Status, id = pi.Id });
    }
    catch (StripeException ex)
    {
        return Results.BadRequest(new
        {
            error = ex.StripeError?.Message ?? ex.Message,
            code = ex.StripeError?.Code,
            type = ex.StripeError?.Type,
            declineCode = ex.StripeError?.DeclineCode
        });
    }
    catch (Exception ex)
    {
        return Results.Problem("Server error: " + ex.Message);
    }
});

app.MapGet("/ping", () => Results.Ok(new { ok = true, time = DateTime.UtcNow }));
app.MapGet("/_diag/resolve/{id}", async (string id, Minis.Services.IStatusRepository repo, CancellationToken ct) =>
{
    if (int.TryParse(id, out var internalId))
    {
        var deliveryId = await repo.GetDeliveryIdByInternalIdAsync(internalId, ct);
        return Results.Ok(new { orderId = internalId, deliveryId });
    }
    // If caller passes a GUID, just echo it back
    return Results.Ok(new { deliveryId = id });
});
// Upsert delivery status for an order (used by your webhook handlers too)
app.MapPost("/orders/{orderId:int}/status", async (
    int orderId,
    DeliveryStatusUpdate body,
    IConfiguration cfg) =>
{
    // 1) Base path from config (falls back to WebRoot if not set)
    var basePath = cfg["Tracking:BasePath"]; // e.g. C:\inetpub\ftproot\Native\Native\images\json
    if (string.IsNullOrWhiteSpace(basePath))
    {
        // last-resort fallback to webroot/publish
        basePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "json");
    }

    // 2) Ensure /tracking exists
    var trackingDir = Path.Combine(basePath, "tracking");
    Directory.CreateDirectory(trackingDir);

    // 3) Write JSON
    var filePath = Path.Combine(trackingDir, $"order-{orderId}.json");
    var json = System.Text.Json.JsonSerializer.Serialize(new
    {
        orderId,
        provider = body.Provider,
        status = body.Status,
        etaMinutes = body.EtaMinutes,
        driver = new { body.DriverName, body.DriverPhone, body.Vehicle, body.Plate },
        location = new { body.Lat, body.Lng, body.Accuracy, body.Bearing },
        updatedAtUtc = DateTime.UtcNow
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    await System.IO.File.WriteAllTextAsync(filePath, json);

    // 4) Public URL that maps to that folder (adjust to your real domain/path)
    var publicUrl = "/images/json/tracking/order-" + orderId + ".json";

    return Results.Ok(new { ok = true, orderId, url = publicUrl, path = filePath });
});

app.MapPost("/diag-email", async (HttpContext ctx, IEmailService email) =>
{
    var q = ctx.Request.Query;
    var to = string.IsNullOrWhiteSpace(q["to"]) ? "eilamomer@gmail.com" : q["to"].ToString();
    var from = string.IsNullOrWhiteSpace(q["from"]) ? null : q["from"].ToString();
    var fromName = string.IsNullOrWhiteSpace(q["fromName"]) ? "Beigel Bake" : q["fromName"].ToString();
    var subject = string.IsNullOrWhiteSpace(q["subject"]) ? "Brevo diagnostic" : q["subject"].ToString();

    var html = "<!doctype html><html><body><h2>Diagnostic</h2><p>If you see this, SMTP worked.</p></body></html>";
    await email.SendRawHtmlAsync(to, subject, html, fromName, from);

    return Results.Ok(new { sentTo = to, from = from ?? "(default)" });
});


// Simple invoice from posted JSON (same shape you tested earlier)
app.MapPost("/diag-invoice-classic", async (
    HttpContext ctx,
    IConfiguration cfg,
    IEmailService email) =>
{
    try
    {
        var to = string.IsNullOrWhiteSpace(ctx.Request.Query["to"])
                   ? "eilamomer@gmail.com" : ctx.Request.Query["to"].ToString();
        var from = string.IsNullOrWhiteSpace(ctx.Request.Query["from"])
                   ? "beigelbake@hoodapp.co.uk" : ctx.Request.Query["from"].ToString();
        var fromName = string.IsNullOrWhiteSpace(ctx.Request.Query["fromName"])
                   ? "Beigel Bake" : ctx.Request.Query["fromName"].ToString();

        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var raw = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw))
            return Results.BadRequest(new { error = "Empty JSON body" });

        await InvoiceEmailHelper.SendInvoiceFromRawJsonAsync(
            rawJson: raw,
            to: to,
            from: from,
            fromName: fromName,
            ctx: ctx,
            email: email
        );

        return Results.Json(new { sentTo = to, ok = true });
    }
    catch (Exception ex)
    {
        return Results.Problem($"diag-invoice-classic failed: {ex.Message}");
    }
});
static string StripTagsToText(string html)
{
    if (string.IsNullOrWhiteSpace(html)) return "";
    var noTags = System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty);
    return System.Net.WebUtility.HtmlDecode(noTags);
}
// Quick readback (handy for testing)
app.MapGet("/orders/{orderId:int}/status", async (int orderId, IConfiguration config) =>
{
    var baseDir = config["StatusJson:OutputDir"]
        ?? @"C:\inetpub\ftproot\Native\Native\images\orders";
    var filePath = Path.Combine(baseDir, $"{orderId}.json");
    if (!System.IO.File.Exists(filePath)) return Results.NotFound(new { message = "No status file yet." });
    var json = await System.IO.File.ReadAllTextAsync(filePath);
    return Results.Text(json, "application/json");
});

app.MapPost("/diag/push", async (
    [FromBody] PushRequest payload,                 // 👈 body comes here
    [FromServices] IFcmPushService push,           // 👈 resolve from DI
    [FromServices] ILogger<Program> log,
    CancellationToken ct) =>
{
    try
    {
        log.LogInformation("/diag/push → token len={len}, title={title}", payload.Token?.Length, payload.Title);

        if (string.IsNullOrWhiteSpace(payload.Token))
            return Results.BadRequest(new { ok = false, error = "Missing token" });

        var id = await push.SendAsync(
            payload.Token!,
            payload.Title ?? "Ping",
            payload.Body ?? "Hello from Minis",
            payload.Data,
            ct);

        return Results.Ok(new { ok = true, messageId = id });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/diag/push failed");
        return Results.Problem(ex.Message);
    }
});



app.MapPost("/submitOrder", async (
    SubmitOrderRequestDto request,
    HttpContext context,
    IConfiguration config,
    IEmailService email,
    IDeliveryRouter deliveryRouter,
    IHttpClientFactory httpFactory  // ✅ inject instead of app.Services
) =>
{
    string? Corr(string k) =>
        context.Request.Headers.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.ToString()
            : null;

    var correlationId = Corr("X-Request-Id") ?? Corr("Idempotency-Key") ?? Guid.NewGuid().ToString("N");

    try
    {
        // 1) Payment succeeded on device (prefer PI)
        var paymentIntentId = request.StripePaymentIntentId;
        string? effectiveChargeId = null;
        string paymentMethodLabel;

        if (!string.IsNullOrWhiteSpace(paymentIntentId))
        {
            var piSvc = new PaymentIntentService();
            var pi = await piSvc.GetAsync(paymentIntentId);
            if (!string.Equals(pi.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { ok = false, message = "PaymentIntent not succeeded.", status = pi.Status });
            }

            var chSvc = new ChargeService();
            var list = await chSvc.ListAsync(new ChargeListOptions { PaymentIntent = paymentIntentId, Limit = 1 });
            effectiveChargeId = list.Data?.FirstOrDefault()?.Id;
            paymentMethodLabel = "Apple Pay (PaymentIntent)";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.ApplePayToken))
                return Results.BadRequest(new { ok = false, message = "Missing applePayToken or stripePaymentIntentId." });
            paymentMethodLabel = "Apple Pay (Stripe Charge)";
        }

        var subtotal = request.Basket?.Sum(b => b.Price * b.Quantity) ?? 0m;
        var deliveryFee = request.Total - subtotal;

        // 2) Insert MINIS order with retry
        int orderId;
        try
        {
            orderId = await RetryAsync<int>(async ct =>
            {
                var customerId = await OrderService.UpsertCustomerAsync(request.UUID, request.Email, request.Name, config);

                var metadata = new
                {
                    request.Basket,
                    request.Delivery,
                    totals = new { subtotal, deliveryFee, total = request.Total },
                    payment = new
                    {
                        method = paymentMethodLabel,
                        stripeChargeId = effectiveChargeId,
                        stripePaymentIntentId = paymentIntentId
                    },
                    correlationId
                };

                return await OrderService.InsertOrderAsync(customerId, request.MiniAppId, request.Total, metadata, config);
            }, maxAttempts: 3, ct: context.RequestAborted);
        }
        catch (Exception ex)
        {
            await TrySendSosAsync(email,
                subject: "Order insert failed AFTER payment",
                model: new { request, subtotal, deliveryFee, total = request.Total, paymentIntentId, effectiveChargeId, correlationId, error = ex.Message },
                ctx: context);

            var receipt = (paymentIntentId ?? effectiveChargeId ?? "PI_UNKNOWN");
            return Results.Ok(new
            {
                orderId = 0,
                customerId = 0,
                payment = "succeeded",
                dispatch = "pending",
                error = "saved_later",
                receiptId = receipt
            });
        }

        // 2a) WhatsApp ops message (best-effort)
        try
        {
            var waText = BuildOrderSummaryText(orderId, request, subtotal, deliveryFee);
            await SendWhatsAppAsync(context, waText);
        }
        catch { /* ignore */ }

        // 3) Customer email (best-effort)
        try
        {
            var isDelivery = request.Delivery?.IsDelivery ?? false;
            var classicPayload = new
            {
                orderId,
                shopName = "Beigel Bake",
                logoUrl = "https://www.beigelbake.co.uk/cdn/shop/files/main-logo.jpg?v=1672516746&width=400",
                buttonColor = "#c72d22",
                whenText = request.Delivery?.ScheduledFor?.ToString(),
                trackUrl = $"https://hoodapp.co.uk/tracker.html?id={orderId}",
                customer = new { name = request.Name, phone = request.Delivery?.Address?.Phone },
                delivery = new
                {
                    isDelivery,
                    scheduledFor = request.Delivery?.ScheduledFor?.ToString(),
                    address = request.Delivery?.Address is null ? null : new
                    {
                        address1 = request.Delivery.Address.Address1,
                        address2 = request.Delivery.Address.Address2,
                        postcode = request.Delivery.Address.Postcode,
                        phone = request.Delivery.Address.Phone
                    }
                },
                basket = request.Basket?.Select(b => new
                {
                    name = string.IsNullOrWhiteSpace(b.ProductName) ? (b.Name ?? "Item") : b.ProductName,
                    modifiers = b.Modifiers,
                    quantity = b.Quantity,
                    price = b.Price
                }),
                subtotal,
                deliveryFee,
                total = request.Total
            };
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(classicPayload));
            await email.SendSimpleInvoiceAsync(
                to: request.Email ?? "no-reply@minis.app",
                subject: $"Order #{orderId} — {((request.Delivery?.IsDelivery ?? false) ? "Delivery" : "Collection")}",
                payload: doc.RootElement,
                fromName: "Beigel Bake",
                fromEmail: "beigelbake@hoodapp.co.uk",
                bcc: "eilamomer@gmail.com",
                ct: context.RequestAborted
            );
        }
        catch (Exception ex)
        {
            await TrySendSosAsync(email, "Customer email failed AFTER payment",
                new { orderId, request, correlationId, error = ex.Message }, context);
        }

        // 4) Stripe Connect transfer (best-effort)
        string? destinationPaymentId = null;
        try
        {
            var shopAccountId = config["Shop:StripeAccountId"];
            var splitPercentStr = config["Shop:SplitPercent"];
            var splitPercent = string.IsNullOrWhiteSpace(splitPercentStr) ? 0.71m : decimal.Parse(splitPercentStr);
            string? transferId = null;

            if (!string.IsNullOrWhiteSpace(shopAccountId) && !string.IsNullOrWhiteSpace(effectiveChargeId))
            {
                var netToShop = (long)Math.Round(subtotal * 100m * splitPercent, MidpointRounding.AwayFromZero);
                if (netToShop > 0)
                {
                    var transfer = await new TransferService().CreateAsync(new TransferCreateOptions
                    {
                        Amount = netToShop,
                        Currency = "gbp",
                        Destination = shopAccountId,
                        SourceTransaction = effectiveChargeId,
                        Description = $"Order {orderId}"
                    });
                    transferId = transfer.Id;
                    destinationPaymentId = transfer.DestinationPaymentId;
                }
                await OrderService.UpdateTransferMetadataAsync(orderId, effectiveChargeId, transferId, destinationPaymentId, splitPercent, config);
            }
        }
        catch (Exception ex)
        {
            await TrySendSosAsync(email, "Connect transfer failed AFTER payment",
                new { orderId, request, correlationId, error = ex.Message }, context);
        }

        // 5) Create **legacy** order FIRST so we can use its ID as courier reference
        int legacyOrderId = 0;
        try
        {
            legacyOrderId = await LegacySync.SyncToLegacyAsync(
                config,
                submitRequest: request,
                minisOrderId: orderId,
                stripeChargeId: effectiveChargeId ?? "",
                destinationPaymentId: destinationPaymentId,
                subtotal: subtotal,
                deliveryFee: deliveryFee);
        }
        catch (Exception ex)
        {
            await TrySendSosAsync(email, "LegacySync failed AFTER payment",
                new { orderId, request, correlationId, error = ex.Message }, context);
        }

        // 6) Delivery dispatch (best-effort)
        DeliveryJobResult? delivery = null;
        try
        {
            if (request.Delivery?.IsDelivery == true && request.Delivery.Address is not null)
            {
                // Build job request, then override the Reference to legacy id if we have one
                var jobReq = DeliveryMapper.BuildFromRequest(orderId, request, config);
                if (legacyOrderId > 0)
                    jobReq = jobReq with { Reference = legacyOrderId.ToString() }; // ✅ record 'with' to swap reference

                delivery = await deliveryRouter.DispatchAsync(jobReq, context.RequestAborted);

                // Store courier metadata in MINIS
                await OrderService.UpdateDeliveryMetadataAsync(orderId,
                    provider: delivery?.Provider ?? "(null)",
                    deliveryId: delivery?.DeliveryId ?? "(null)",
                    config);

                // Also update legacy with courier ids (best-effort)
                if (legacyOrderId > 0 && delivery is not null)
                {
                    try
                    {
                        await LegacySync.UpdateLegacyCourierAsync(config, legacyOrderId, delivery);
                    }
                    catch (Exception exLeg)
                    {
                        await TrySendSosAsync(email, "Legacy courier-id update failed",
                            new { orderId, legacyOrderId, delivery, correlationId, error = exLeg.Message }, context);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await OrderService.UpdateDeliveryMetadataAsync(orderId, provider: "pending", deliveryId: "", config);
            await TrySendSosAsync(email, "Delivery dispatch failed AFTER payment",
                new { orderId, request, correlationId, error = ex.Message }, context);

            // We already created the legacy order above; keep going with a soft OK
            return Results.Ok(new SubmitOrderOk(orderId, 0, "succeeded", null, "pending", ex.Message));
        }

        // 7) Ops snapshot (best-effort)
        try
        {
            var snapshot = new
            {
                orderId,
                legacyOrderId,
                totals = new { subtotal, deliveryFee, total = request.Total },
                payment = new { effectiveChargeId, paymentIntentId },
                request,
                correlationId
            };
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            var html = $"<pre style=\"font-family:ui-monospace,Menlo,Consolas\">{System.Net.WebUtility.HtmlEncode(json)}</pre>";
            await email.SendRawHtmlAsync("eilamomer@gmail.com", "[MINIS][SNAPSHOT] Order success", html, "Beigel Bake", "beigelbake@hoodapp.co.uk");
        }
        catch { /* best-effort */ }

        // 8) Analytics (best-effort)
        try
        {
            var http = httpFactory.CreateClient(); // ✅ use IHttpClientFactory
            var measurementId = config["GA4:MeasurementId"];
            var apiSecret = config["GA4:ApiSecret"];
            var clientId = request.UUID ?? Guid.NewGuid().ToString();
            _ = AnalyticsGa4.SendPurchaseAsync(http, measurementId!, apiSecret!, clientId,
                request, orderId, subtotal, deliveryFee, context.RequestAborted);
        }
        catch (Exception ex)
        {
            await TrySendSosAsync(email, "Analytics failed AFTER payment",
                new { orderId, request, correlationId, error = ex.Message }, context);
        }

        // 9) Done
        return Results.Ok(new SubmitOrderOk(orderId, 0, "succeeded", delivery, null, null));
    }
    catch (StripeException ex)
    {
        await TrySendSosAsync(email, "Stripe error (outer catch)", new
        {
            request,
            error = ex.StripeError?.Message ?? ex.Message,
            code = ex.StripeError?.Code,
            type = ex.StripeError?.Type,
            declineCode = ex.StripeError?.DeclineCode
        }, context);
        return Results.BadRequest(new { ok = false, message = ex.StripeError?.Message ?? ex.Message });
    }
    catch (Exception ex)
    {
        await TrySendSosAsync(email, "Unhandled server error AFTER payment", new { request, correlationId, error = ex.ToString() }, context);
        return Results.Problem("❌ Internal server error.");
    }
});
app.MapPost("/diag/push-test", async (
    IFcmPushService push,
    IConfiguration cfg,
    CancellationToken ct) =>
{
    // Get token from config or hardcode for now
    var token = cfg["Fcm:TestDeviceToken"]
                ?? "eHgPYU6eVkpNv69MdW23OR:APA91bHIvRL_szC3Mej2KOybOSDq9VSjcjq0xiCevRZXl3-nsgEHJoj7YcxFb-9cvx3TjAj6_fbnll1XmFGuRz_LCjBkKUh4c24Acj9Oox9QC2fqEJWKLaQ";

    var responseId = await push.SendAsync(
        token: token,
        title: "🔥 Test Push",
        body: "This is a test push from /diag/push-test",
        data: new Dictionary<string, string>
        {
            { "orderId", "TEST-123" },
            { "status", "test" }
        },
        ct: ct
    );

    return Results.Ok(new
    {
        ok = true,
        token,
        fcmResponseId = responseId
    });
});

app.MapPost("/webhooks/delivery-status", async (
    [FromBody] DeliveryStatusWebhook payload,
    IStatusRepository repo,
    IFcmPushService push,
    ILogger<Program> log,
    IConfiguration cfg,
    CancellationToken ct) =>
{
    // ---- basic validation
    if (string.IsNullOrWhiteSpace(payload.OrderId))
        return Results.BadRequest(new { error = "orderId is required" });

    var statusCode = StatusMapper.ToCode(payload.DeliveryStatus);
    if (statusCode < 0)
        return Results.Ok(new { ok = true, ignored = true });

    log.LogInformation("[WEBHOOK] raw orderId={rawId} statusText={statusText} -> statusCode={code}",
        payload.OrderId, payload.DeliveryStatus, statusCode);

    // ---- figure out DB update mode (by internal Id vs deliveryId)
    string deliveryKey = payload.OrderId!;
    int? internalOrderId = null;
    if (int.TryParse(payload.OrderId, out var parsed))
        internalOrderId = parsed;

    // ---- build status update
    var update = new StatusUpdate
    {
        DeliveryId = deliveryKey, // used when not numeric
        StatusCode = statusCode,
        Latitude = payload.Driver?.Location?.Lat,
        Longitude = payload.Driver?.Location?.Long,
        DriverName = payload.Driver?.Name,
        DriverPhone = payload.Driver?.Phone,
        PickupTimeUtc = TryParseDateTimeUtc(payload.Eta?.Pickup)
    };

    // ---- write to DB (only if changed)
    int rows;
    if (internalOrderId.HasValue)
    {
        rows = await repo.UpdateOrderStatusByIdAsync(internalOrderId.Value, update, ct);
        log.LogInformation("[DB] UpdateById id={id} rowsAffected={rows}", internalOrderId, rows);
    }
    else
    {
        rows = await repo.UpdateOrderStatusAsync(update, ct);
        log.LogInformation("[DB] UpdateByDeliveryId key={key} rowsAffected={rows}", deliveryKey, rows);
    }

    // ---- tracking JSON (change detection by file content)
    var basePath = cfg["StatusJson:OutputDir"] ?? cfg["Tracking:BasePath"]
                   ?? Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "json");
    var trackingDir = Path.Combine(basePath, "tracking");
    Directory.CreateDirectory(trackingDir);

    // Use the provided key for the file name (so curl with numeric or GUID is consistent)
    var filePath = Path.Combine(trackingDir, $"order-{deliveryKey}.json");

    int? oldStatus = null;
    if (System.IO.File.Exists(filePath))
    {
        try
        {
            using var doc = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(filePath, ct));
            if (doc.RootElement.TryGetProperty("status", out var s) && s.TryGetInt32(out var v)) oldStatus = v;
        }
        catch (Exception ex) { log.LogWarning(ex, "[FILE] failed to read prior status for {key}", deliveryKey); }
    }

    var changed = rows > 0 || oldStatus != statusCode;
    if (changed)
    {
        var json = JsonSerializer.Serialize(new
        {
            orderId = deliveryKey,
            status = statusCode,
            deliveryStatus = payload.DeliveryStatus,
            driver = payload.Driver,
            eta = payload.Eta,
            updatedAtUtc = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true });

        await System.IO.File.WriteAllTextAsync(filePath, json, ct);
        log.LogInformation("[FILE] wrote {path}", filePath);
    }
    else
    {
        log.LogInformation("[FILE] unchanged (oldStatus={old}, newStatus={new})", oldStatus, statusCode);
    }

    // ---- HARD-CODED PUSH TOKEN (for testing)
    var testFcmToken = cfg["Fcm:TestDeviceToken"] // optional override from config
                       ?? "eHgPYU6eVkpNv69MdW23OR:APA91bHIvRL_szC3Mej2KOybOSDq9VSjcjq0xiCevRZXl3-nsgEHJoj7YcxFb-9cvx3TjAj6_fbnll1XmFGuRz_LCjBkKUh4c24Acj9Oox9QC2fqEJWKLaQ";

    if (changed && !string.IsNullOrWhiteSpace(testFcmToken))
    {
        try
        {
            var orderLabel = internalOrderId?.ToString() ?? deliveryKey;

            if (statusCode == 4) // in_transit
            {
                await push.SendAsync(
                    token: testFcmToken,
                    title: "Your order is on the way 🚚",
                    body: $"Order #{orderLabel} is out for delivery",
                    data: new Dictionary<string, string> {
                        { "orderId", orderLabel }, { "status", "in_transit" }
                    },
                    ct: ct
                );
                log.LogInformation("[PUSH] sent in_transit");
            }
            else if (statusCode == 5) // delivered
            {
                await push.SendAsync(
                    token: testFcmToken,
                    title: "Order delivered ✅",
                    body: $"Order #{orderLabel} has been delivered",
                    data: new Dictionary<string, string> {
                        { "orderId", orderLabel }, { "status", "delivered" }
                    },
                    ct: ct
                );
                log.LogInformation("[PUSH] sent delivered");
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[PUSH] failed");
        }
    }

    return Results.Ok(new
    {
        ok = true,
        changed,
        statusCode,
        deliveryId = deliveryKey,
        orderId = internalOrderId,
        rowsAffected = rows
    });
});



Minis.Api.MenuApi.Register(app);
Minis.Api.Customization.Register(app);

app.Run();


static DateTimeOffset? TryParseDateTimeUtc(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return null;
    if (DateTimeOffset.TryParse(s, out var dto)) return dto.ToUniversalTime();
    return null;
}

static bool AreTotalsClose(decimal a, decimal b, decimal tol) => Math.Abs(a - b) <= tol;

static string? GetCorrelationId(HttpContext ctx)
{
    if (ctx.Request.Headers.TryGetValue("X-Request-Id", out var v) && !string.IsNullOrWhiteSpace(v))
        return v.ToString();
    if (ctx.Request.Headers.TryGetValue("Idempotency-Key", out var v2) && !string.IsNullOrWhiteSpace(v2))
        return v2.ToString();
    return null;
}

static string BuildOrderSummaryText(int orderId, SubmitOrderRequestDto req, decimal subtotal, decimal deliveryFee)
{
    // mode
    var isDelivery = req.Delivery?.IsDelivery ?? false;
    var mode = isDelivery ? "Delivery" : "Collection";

    // When text (prefer ScheduledFor; fall back to PickupTime; else ASAP)
    var when =
        (!string.IsNullOrWhiteSpace(req.Delivery?.ScheduledFor?.ToString()) ? req.Delivery!.ScheduledFor!.ToString() :
        !string.IsNullOrWhiteSpace(req.Delivery?.PickupTime?.ToString()) ? req.Delivery!.PickupTime!.ToString() :
        "ASAP");

    // Address (short)
    string addr = "";
    if (isDelivery && req.Delivery?.Address is not null)
    {
        var a = req.Delivery.Address;
        var line = new[] { a.Address1, a.Postcode }.Where(s => !string.IsNullOrWhiteSpace(s));
        addr = string.Join(", ", line);
    }

    // Items lines: 2x Bagel (Cheese), 1x Coffee
    var items = req.Basket
     .Select(b =>
     {
         var name = string.IsNullOrWhiteSpace(b.ProductName) ? (b.Name ?? "Item") : b.ProductName;
         var mods = string.IsNullOrWhiteSpace(b.Modifiers) ? "" : $" ({b.Modifiers})";
         return $"{b.Quantity}× {name}{mods}";
     })
     .ToList();

    var total = subtotal + deliveryFee;

    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"[NEW ORDER] #{orderId} — {mode}");
    if (!string.IsNullOrWhiteSpace(req.Name)) sb.AppendLine($"Name: {req.Name}");
    sb.AppendLine($"When: {when}");
    if (isDelivery && !string.IsNullOrWhiteSpace(addr)) sb.AppendLine($"To: {addr}");
    sb.AppendLine("Items:");
    foreach (var line in items) sb.AppendLine($"• {line}");
    if (deliveryFee > 0) sb.AppendLine($"Delivery: £{deliveryFee:0.00}");
    sb.AppendLine($"Total: £{total:0.00}");
    return sb.ToString();
}
static async Task SendWhatsAppAsync(HttpContext ctx, string text)
{
    try
    {
        var http = ctx.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
        var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();

        var instance = cfg["UltraMsg:InstanceId"];   // e.g. "instance116704"
        var token = cfg["UltraMsg:Token"];        // e.g. "t8hpeqesth100p56"
        var to = cfg["UltraMsg:To"];           // e.g. "120363229534969261@g.us"

        if (string.IsNullOrWhiteSpace(instance) ||
            string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(to))
            return; // not configured → skip

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.ultramsg.com/{instance}/messages/chat");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["to"] = to,
            ["body"] = text
        });

        using var resp = await http.SendAsync(req, ctx.RequestAborted);
        _ = await resp.Content.ReadAsStringAsync(ctx.RequestAborted); // optional
    }
    catch { /* best-effort */ }
}

static async Task TrySendSosAsync(
    IEmailService email,
    string subject,
    object model,
    HttpContext ctx,
    string to = "eilamomer@gmail.com",           // ops inbox
    string fromName = "Beigel Bake",
    string fromEmail = "beigelbake@hoodapp.co.uk")
{
    // --- 1) Email (rich JSON) ---
    try
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            model, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var html = $"<pre style=\"font-family:ui-monospace,Menlo,Consolas\">{System.Net.WebUtility.HtmlEncode(json)}</pre>";
        await email.SendRawHtmlAsync(
            to,
            $"[MINIS][SOS] {subject}",
            html,
            fromName,
            fromEmail
           
        );
    }
    catch { /* best-effort */ }

    // --- 2) WhatsApp (short text) ---
    try
    {
        // Resolve IHttpClientFactory and IConfiguration from the request's service provider
        var httpFactory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>();
        var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();

        var instance = cfg["UltraMsg:InstanceId"];
        var token = cfg["UltraMsg:Token"];
        var waTo = cfg["UltraMsg:To"];

        if (string.IsNullOrWhiteSpace(instance) ||
            string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(waTo))
        {
            return; // not configured → skip
        }

        string Shorten(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        var compact = System.Text.Json.JsonSerializer.Serialize(
            model, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });

        var text = $"[SOS] {subject}\n{Shorten(compact, 500)}";

        var http = httpFactory.CreateClient();
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.ultramsg.com/{instance}/messages/chat");

        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["to"] = waTo,
            ["body"] = text
        });

        using var resp = await http.SendAsync(req, ctx.RequestAborted);
        _ = await resp.Content.ReadAsStringAsync(ctx.RequestAborted); // optional read
    }
    catch { /* best-effort */ }
}



static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> op, int maxAttempts, CancellationToken ct)
{
    var attempt = 0;
    var rnd = new Random();
    Exception? last = null;

    while (attempt < maxAttempts)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return await op(ct);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            last = ex;
            attempt++;
            if (attempt >= maxAttempts) break;
            var backoff = TimeSpan.FromMilliseconds(150 * Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(rnd.Next(0, 200));
            await Task.Delay(backoff, ct);
        }
    }
    throw last ?? new Exception("Operation failed after retries.");

    static bool IsTransient(Exception ex)
        => ex is HttpRequestException || ex is TaskCanceledException;
}

public record CreatePIRequest(
    long Amount,
    string Currency,
    string PaymentMethod,
    string? Description,
    Dictionary<string, string>? Metadata
);
public record SubmitOrderOk(
    int orderId,
    int customerId,
    string payment,
    DeliveryJobResult? delivery,
    string? dispatch,
    string? error
)
    
    
    ;
record PushReq(string token, string? title, string? body, Dictionary<string, string>? data);

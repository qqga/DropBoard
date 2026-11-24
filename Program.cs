using System.Threading.RateLimiting;
using DropBoard.Hubs;
using DropBoard.Models;
using DropBoard.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

var limits = new BoardLimits();
builder.Services.AddSingleton(limits);
builder.Services.AddSingleton<RoomStorageService>();
builder.Services.AddHostedService<CleanupBackgroundService>();

// Жесткие лимиты на размер HTTP-запросов
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = limits.MaxFileSizeBytes + (1024 * 1024); // +1 МБ на заголовки multipart
    options.Limits.MaxConcurrentConnections = 1000;
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = limits.MaxFileSizeBytes;
});

// SignalR с ограничением размера сообщения (только для текста и метаданных)
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 128 * 1024; // 128 КБ макс. на фрейм
});

// Защита от спама по IP
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("UploadPolicy", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();

app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

// Загрузка файлов потоком
app.MapPost("/api/rooms/{roomId}/files", async (
    string roomId,
    HttpRequest request,
    RoomStorageService storage,
    IHubContext<BoardHub> hubContext,
    CancellationToken ct) =>
{
    if (!RoomStorageService.IsValidRoomId(roomId))
        return Results.BadRequest(new { error = "Некорректный ID комнаты." });

    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Ожидается multipart/form-data." });

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file == null)
        return Results.BadRequest(new { error = "Файл не передан." });

    bool isBurn = string.Equals(form["burn"], "true", StringComparison.OrdinalIgnoreCase);

    try
    {
        var item = await storage.AddFileItemAsync(roomId, file, isBurn, ct);
        var dto = BoardHub.MapToDto(item);
        await hubContext.Clients.Group(roomId).SendAsync("ItemAdded", dto, ct);
        return Results.Ok(dto);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireRateLimiting("UploadPolicy");

// Безопасное скачивание файлов (Zero-Execution)
app.MapGet("/api/rooms/{roomId}/files/{itemId}", async (
    string roomId,
    string itemId,
    HttpContext httpContext,
    RoomStorageService storage,
    IHubContext<BoardHub> hubContext) =>
{
    var item = storage.GetFileForDownload(roomId, itemId, out bool burnedNow);
    if (item == null || string.IsNullOrEmpty(item.StoredFilePath) || !File.Exists(item.StoredFilePath))
        return Results.NotFound("Файл не найден или уже самоуничтожился.");

    if (burnedNow)
    {
        await hubContext.Clients.Group(roomId).SendAsync("ItemBurned", itemId);
        // Удаляем физический файл сразу после завершения отправки ответа клиенту
        var pathToDelete = item.StoredFilePath;
        httpContext.Response.OnCompleted(() =>
        {
            RoomStorageService.DeletePhysicalFile(pathToDelete);
            return Task.CompletedTask;
        });
    }

    httpContext.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(
        item.StoredFilePath,
        contentType: "application/octet-stream",
        fileDownloadName: item.FileName,
        enableRangeProcessing: !burnedNow);
});

app.MapHub<BoardHub>("/hubs/board");

// Fallback для красивых URL вида /my-room
app.MapFallbackToFile("index.html");

app.Run();

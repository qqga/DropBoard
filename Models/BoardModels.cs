using System.Collections.Concurrent;

namespace DropBoard.Models;

public class BoardLimits
{
    public int MaxRooms { get; init; } = 500;
    public int MaxItemsPerRoom { get; init; } = 40;
    public int MaxTextLength { get; init; } = 50_000; // 50 КБ текста
    public long MaxFileSizeBytes { get; init; } = 15 * 1024 * 1024; // 15 МБ на файл
    public long MaxRoomStorageBytes { get; init; } = 50 * 1024 * 1024; // 50 МБ на комнату
    public long MaxGlobalStorageBytes { get; init; } = 500 * 1024 * 1024; // 500 МБ на весь сервер
    public TimeSpan RoomTtl { get; init; } = TimeSpan.FromHours(24);
}

public enum ItemType
{
    Text,
    File
}

public class BoardItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public ItemType Type { get; init; }
    public string? Content { get; set; } // Для текста (null, если это скрытый секрет до раскрытия)
    public string? SecretContent { get; set; } // Хранится на сервере до первого клика "Показать"
    public string? FileName { get; init; }
    public string? StoredFilePath { get; init; }
    public long FileSize { get; init; }
    public bool IsBurnAfterRead { get; init; }
    public bool IsConsumed { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public class Room
{
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
    public ConcurrentQueue<BoardItem> Items { get; } = new();
    public object SyncLock { get; } = new();

    public long TotalFileBytes => Items.Where(i => i.Type == ItemType.File && !i.IsConsumed).Sum(i => i.FileSize);
}

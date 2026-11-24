using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DropBoard.Models;

namespace DropBoard.Services;

public partial class RoomStorageService
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly BoardLimits _limits;
    private readonly string _storageRoot;
    private readonly ILogger<RoomStorageService> _logger;

    [GeneratedRegex("^[a-zA-Z0-9_-]{3,32}$")]
    private static partial Regex RoomIdRegex();

    public RoomStorageService(BoardLimits limits, ILogger<RoomStorageService> logger)
    {
        _limits = limits;
        _logger = logger;
        _storageRoot = Path.Combine(Path.GetTempPath(), "dropboard_files");
        Directory.CreateDirectory(_storageRoot);
    }

    public BoardLimits Limits => _limits;
    public string StorageRoot => _storageRoot;

    public static bool IsValidRoomId(string roomId) => !string.IsNullOrWhiteSpace(roomId) && RoomIdRegex().IsMatch(roomId);

    public Room GetOrCreateRoom(string roomId)
    {
        if (!IsValidRoomId(roomId))
            throw new ArgumentException("Некорректный ID комнаты (3-32 символа: латиница, цифры, - или _).");

        if (_rooms.TryGetValue(roomId, out var existing))
        {
            existing.LastActivity = DateTimeOffset.UtcNow;
            return existing;
        }

        // Если достигнут лимит комнат — удаляем самую старую неактивную
        if (_rooms.Count >= _limits.MaxRooms)
        {
            var oldest = _rooms.Values.OrderBy(r => r.LastActivity).FirstOrDefault();
            if (oldest != null)
            {
                RemoveRoom(oldest.Id);
            }
        }

        var room = new Room { Id = roomId };
        return _rooms.GetOrAdd(roomId, room);
    }

    public bool TryGetRoom(string roomId, out Room? room) => _rooms.TryGetValue(roomId, out room);

    public BoardItem AddTextItem(string roomId, string text, bool isBurnAfterRead)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Текст не может быть пустым.");

        if (text.Length > _limits.MaxTextLength)
            throw new ArgumentException($"Превышен лимит текста ({_limits.MaxTextLength} символов).");

        var room = GetOrCreateRoom(roomId);
        var item = new BoardItem
        {
            Type = ItemType.Text,
            Content = isBurnAfterRead ? null : text,
            SecretContent = isBurnAfterRead ? text : null,
            IsBurnAfterRead = isBurnAfterRead
        };

        EnqueueItemWithEviction(room, item);
        return item;
    }

    public async Task<BoardItem> AddFileItemAsync(string roomId, IFormFile file, bool isBurnAfterRead, CancellationToken ct)
    {
        if (file.Length == 0)
            throw new ArgumentException("Файл пуст.");

        if (file.Length > _limits.MaxFileSizeBytes)
            throw new ArgumentException($"Файл слишком большой (макс. {_limits.MaxFileSizeBytes / (1024 * 1024)} МБ).");

        var room = GetOrCreateRoom(roomId);

        if (room.TotalFileBytes + file.Length > _limits.MaxRoomStorageBytes)
            throw new InvalidOperationException($"Превышен лимит места в комнате ({_limits.MaxRoomStorageBytes / (1024 * 1024)} МБ).");

        EnsureGlobalStorageQuota(file.Length);

        var safeFileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(safeFileName)) safeFileName = "file.bin";
        if (safeFileName.Length > 120) safeFileName = safeFileName[^120..];

        var roomDir = Path.Combine(_storageRoot, room.Id);
        Directory.CreateDirectory(roomDir);

        var storedFileName = $"{Guid.NewGuid():N}.dat";
        var fullPath = Path.Combine(roomDir, storedFileName);

        await using (var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await file.CopyToAsync(fs, ct);
        }

        var item = new BoardItem
        {
            Type = ItemType.File,
            FileName = safeFileName,
            StoredFilePath = fullPath,
            FileSize = file.Length,
            IsBurnAfterRead = isBurnAfterRead
        };

        EnqueueItemWithEviction(room, item);
        return item;
    }

    public string? RevealSecretText(string roomId, string itemId, out bool burnedNow)
    {
        burnedNow = false;
        if (!_rooms.TryGetValue(roomId, out var room)) return null;

        lock (room.SyncLock)
        {
            var item = room.Items.FirstOrDefault(i => i.Id == itemId && i.Type == ItemType.Text);
            if (item == null || item.IsConsumed || !item.IsBurnAfterRead) return null;

            var secret = item.SecretContent;
            item.SecretContent = null;
            item.Content = null;
            item.IsConsumed = true;
            burnedNow = true;
            room.LastActivity = DateTimeOffset.UtcNow;
            return secret;
        }
    }

    public BoardItem? GetFileForDownload(string roomId, string itemId, out bool burnedNow)
    {
        burnedNow = false;
        if (!_rooms.TryGetValue(roomId, out var room)) return null;

        lock (room.SyncLock)
        {
            var item = room.Items.FirstOrDefault(i => i.Id == itemId && i.Type == ItemType.File);
            if (item == null || item.IsConsumed) return null;

            room.LastActivity = DateTimeOffset.UtcNow;
            if (item.IsBurnAfterRead)
            {
                item.IsConsumed = true;
                burnedNow = true;
            }
            return item;
        }
    }

    public void DeleteItem(string roomId, string itemId)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return;

        lock (room.SyncLock)
        {
            var item = room.Items.FirstOrDefault(i => i.Id == itemId);
            if (item == null) return;

            item.IsConsumed = true;
            item.Content = null;
            item.SecretContent = null;
            DeletePhysicalFile(item.StoredFilePath);
        }
    }

    private void EnqueueItemWithEviction(Room room, BoardItem newItem)
    {
        lock (room.SyncLock)
        {
            room.LastActivity = DateTimeOffset.UtcNow;
            room.Items.Enqueue(newItem);

            while (room.Items.Count > _limits.MaxItemsPerRoom && room.Items.TryDequeue(out var evicted))
            {
                DeletePhysicalFile(evicted.StoredFilePath);
            }
        }
    }

    private void EnsureGlobalStorageQuota(long incomingBytes)
    {
        long currentTotal = _rooms.Values.Sum(r => r.TotalFileBytes);
        if (currentTotal + incomingBytes <= _limits.MaxGlobalStorageBytes) return;

        // Вытесняем самые старые комнаты, пока не освободится место
        foreach (var oldRoom in _rooms.Values.OrderBy(r => r.LastActivity).ToList())
        {
            RemoveRoom(oldRoom.Id);
            currentTotal = _rooms.Values.Sum(r => r.TotalFileBytes);
            if (currentTotal + incomingBytes <= _limits.MaxGlobalStorageBytes) break;
        }
    }

    public void RemoveRoom(string roomId)
    {
        if (_rooms.TryRemove(roomId, out var room))
        {
            foreach (var item in room.Items)
            {
                DeletePhysicalFile(item.StoredFilePath);
            }
            var roomDir = Path.Combine(_storageRoot, roomId);
            try
            {
                if (Directory.Exists(roomDir)) Directory.Delete(roomDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить директорию комнаты {RoomId}", roomId);
            }
        }
    }

    public void CleanupExpiredRooms()
    {
        var cutoff = DateTimeOffset.UtcNow - _limits.RoomTtl;
        var expiredIds = _rooms.Values
            .Where(r => r.LastActivity < cutoff)
            .Select(r => r.Id)
            .ToList();

        foreach (var id in expiredIds)
        {
            RemoveRoom(id);
        }

        if (expiredIds.Count > 0)
        {
            _logger.LogInformation("Очищено протухших комнат: {Count}", expiredIds.Count);
        }
    }

    public static void DeletePhysicalFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* Игнорируем блокировки при параллельной очистке */ }
    }
}

using DropBoard.Models;
using DropBoard.Services;
using Microsoft.AspNetCore.SignalR;

namespace DropBoard.Hubs;

public class BoardHub : Hub
{
    private readonly RoomStorageService _storage;

    public BoardHub(RoomStorageService storage)
    {
        _storage = storage;
    }

    public async Task<object> JoinRoom(string roomId)
    {
        var room = _storage.GetOrCreateRoom(roomId);
        await Groups.AddToGroupAsync(Context.ConnectionId, room.Id);

        var items = room.Items
            .Where(i => !i.IsConsumed)
            .Select(MapToDto)
            .ToList();

        return new
        {
            roomId = room.Id,
            items,
            limits = new
            {
                maxFileSizeMb = _storage.Limits.MaxFileSizeBytes / (1024 * 1024),
                maxTextLength = _storage.Limits.MaxTextLength
            }
        };
    }

    public async Task SendText(string roomId, string text, bool isBurnAfterRead)
    {
        CheckRateLimit();
        var item = _storage.AddTextItem(roomId, text, isBurnAfterRead);
        await Clients.Group(roomId).SendAsync("ItemAdded", MapToDto(item));
    }

    public async Task<string?> RevealSecret(string roomId, string itemId)
    {
        CheckRateLimit();
        var secret = _storage.RevealSecretText(roomId, itemId, out bool burnedNow);
        if (burnedNow)
        {
            await Clients.OthersInGroup(roomId).SendAsync("ItemBurned", itemId);
        }
        return secret;
    }

    public async Task DeleteItem(string roomId, string itemId)
    {
        CheckRateLimit();
        _storage.DeleteItem(roomId, itemId);
        await Clients.Group(roomId).SendAsync("ItemRemoved", itemId);
    }

    private void CheckRateLimit()
    {
        var now = DateTimeOffset.UtcNow;
        if (Context.Items["RateWindowStart"] is not DateTimeOffset windowStart || (now - windowStart).TotalSeconds > 10)
        {
            Context.Items["RateWindowStart"] = now;
            Context.Items["RateCount"] = 1;
            return;
        }

        int count = (int)(Context.Items["RateCount"] ?? 0) + 1;
        Context.Items["RateCount"] = count;

        if (count > 25)
            throw new HubException("Слишком много запросов. Подождите пару секунд.");
    }

    public static object MapToDto(BoardItem item) => new
    {
        id = item.Id,
        type = item.Type.ToString().ToLowerInvariant(),
        content = item.Content,
        fileName = item.FileName,
        fileSize = item.FileSize,
        isBurnAfterRead = item.IsBurnAfterRead,
        createdAt = item.CreatedAt
    };
}

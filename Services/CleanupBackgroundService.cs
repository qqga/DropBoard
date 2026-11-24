namespace DropBoard.Services;

public class CleanupBackgroundService : BackgroundService
{
    private readonly RoomStorageService _storage;

    public CleanupBackgroundService(RoomStorageService storage)
    {
        _storage = storage;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            _storage.CleanupExpiredRooms();
        }
    }
}

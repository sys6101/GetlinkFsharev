using GetlinkFshare.Services;
using GetlinkFshare.SignalRHub;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;

namespace GetlinkFshare.Workers
{
    public class FshareInfoWorker : BackgroundService
    {
        private readonly ILogger<FshareInfoWorker> _logger;
        private readonly PuppeteerService _puppeteerService;
        private readonly IHubContext<FshareInfoHub> _hubContext;
        private readonly IMemoryCache _cache;
        public const string CacheKey = "LatestStorageInfo";

        public FshareInfoWorker(
            ILogger<FshareInfoWorker> logger,
            PuppeteerService puppeteerService,
            IHubContext<FshareInfoHub> hubContext,
            IMemoryCache cache)
        {
            _logger = logger;
            _puppeteerService = puppeteerService;
            _hubContext = hubContext;
            _cache = cache;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Fshare Info Worker đang khởi động.");

            // Chờ một chút để đảm bảo các dịch vụ khác đã sẵn sàng, chỉ chạy một lần duy nhất.
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            // --- Chạy lần đầu tiên ngay khi khởi động ---
            await RunTaskAsync(stoppingToken);

            // --- Bắt đầu vòng lặp lên lịch cho các lần chạy tiếp theo ---
            while (!stoppingToken.IsCancellationRequested)
            {
                // Tính toán thời gian chờ đến đầu giờ tiếp theo
                var now = DateTime.Now;
                var nextRunTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(1);
                var delay = nextRunTime - now;
                await Task.Delay(delay, stoppingToken);
                await RunTaskAsync(stoppingToken);
            }
        }
        private async Task RunTaskAsync(CancellationToken stoppingToken)
        {
            try
            {
                var storageInfo = await _puppeteerService.GetStorageInfoAsync();
                if (storageInfo != null)
                {
                    storageInfo.LastUpdated = DateTime.UtcNow;
                    var cacheEntryOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(TimeSpan.FromHours(2));
                    _cache.Set(CacheKey, storageInfo, cacheEntryOptions);

                    await _hubContext.Clients.All.SendAsync("ReceiveStorageInfo", storageInfo, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Đã xảy ra lỗi trong tác vụ của Fshare Info Worker.");
            }
        }
    }
}

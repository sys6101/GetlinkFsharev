using GetlinkFshare.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.SignalR;
using GetlinkFshare.Workers;

namespace GetlinkFshare.SignalRHub              
{
    [Authorize]
    public class FshareInfoHub : Hub
    {
        private readonly IMemoryCache _cache;

        // Inject cache và logger
        public FshareInfoHub(IMemoryCache cache)
        {
            _cache = cache;
        }

        /// <summary>
        /// Được gọi mỗi khi một client mới kết nối thành công.
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            // Thử lấy thông tin mới nhất từ cache
            if (_cache.TryGetValue(FshareInfoWorker.CacheKey, out StorageInfo latestInfo))
            {
                // Nếu có, gửi nó cho chính client vừa kết nối (Clients.Caller)
                await Clients.Caller.SendAsync("ReceiveStorageInfo", latestInfo);
               
            }

            await base.OnConnectedAsync();
        }
    }
}

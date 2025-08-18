using GetlinkFshare.Models;
using GetlinkFshare.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Text.RegularExpressions;

namespace GetlinkFshare.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ScrapingController : ControllerBase
    {
        private readonly PuppeteerService _puppeteerService;
        private readonly ILogger<ScrapingController> _logger;
        private readonly IMemoryCache _cache;

        public ScrapingController(PuppeteerService puppeteerService, ILogger<ScrapingController> logger, IMemoryCache cache)
        {
            _puppeteerService = puppeteerService;
            _logger = logger;
            _cache = cache;
        }

        
        [HttpGet("prepare-download")]
        [Authorize]
        public async Task<IActionResult> PrepareDownload([FromQuery] string fshareUrl, [FromQuery] string? filePassword = null)
        {
            if (string.IsNullOrEmpty(fshareUrl) || !Uri.TryCreate(fshareUrl, UriKind.Absolute, out _))
            {
                return BadRequest("Fshare URL không hợp lệ.");
            }

            try
            {
                // *** ĐÃ CẬP NHẬT: Truyền mật khẩu (nếu có) vào service ***
                var downloadInfo = await _puppeteerService.GetDownloadInfoAsync(fshareUrl, filePassword);

                if (downloadInfo == null)
                {
                    return StatusCode(500, "Không thể lấy thông tin tải file từ Fshare.");
                }

                const long tenGigabytes = 10L * 1024 * 1024 * 1024; // 10 GB in bytes
                if (downloadInfo.FileSize.HasValue && downloadInfo.FileSize.Value > tenGigabytes)
                {
                    var fileSizeInGB = Math.Round(downloadInfo.FileSize.Value / (1024.0 * 1024.0 * 1024.0), 2);
                    _logger.LogWarning("Yêu cầu bị từ chối do file quá lớn: {FileName} ({FileSize} GB)", downloadInfo.FileName, fileSizeInGB);
                    return BadRequest($"File quá lớn ({fileSizeInGB} GB). Chỉ hỗ trợ các file có dung lượng dưới 10 GB.");
                }

                var token = Guid.NewGuid().ToString();
                var cacheEntryOptions = new MemoryCacheEntryOptions()
                    .SetSlidingExpiration(TimeSpan.FromHours(2));

                _cache.Set(token, downloadInfo, cacheEntryOptions);

                var proxyUrl = Url.Action("ExecuteDownload", "Scraping", new { token = token }, Request.Scheme);

                return Ok(new { proxyUrl, fileName = downloadInfo.FileName, fileSize = downloadInfo.FileSize });
            }
            catch (Exception ex)
            {
                // Trả về lỗi một cách thân thiện hơn cho client
                _logger.LogError(ex, "Lỗi khi chuẩn bị link tải cho {Url}", fshareUrl);
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("execute-download")]
        [AllowAnonymous]
        public async Task ExecuteDownload([FromQuery] string token)
        {
            if (string.IsNullOrEmpty(token) || !_cache.TryGetValue(token, out DownloadInfo downloadInfo))
            {
                Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await Response.WriteAsync("Link tải không hợp lệ hoặc đã hết hạn.");
                return;
            }

            try
            {
                var cookieContainer = new CookieContainer();
                // *** ĐÃ SỬA LỖI: Sao chép tất cả các thuộc tính của cookie ***
                foreach (var cookieParam in downloadInfo.Cookies)
                {
                    var cookie = new Cookie
                    {
                        Name = cookieParam.Name,
                        Value = cookieParam.Value,
                        Domain = cookieParam.Domain,
                        Path = cookieParam.Path,
                        // *** ĐÃ SỬA LỖI: Xử lý chính xác các thuộc tính nullable boolean ***
                        Secure = cookieParam.Secure ?? false,
                        HttpOnly = cookieParam.HttpOnly ?? false
                    };
                    if (cookieParam.Expires.HasValue && cookieParam.Expires.Value > 0)
                    {
                        cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)cookieParam.Expires.Value).DateTime;
                    }

                    cookieContainer.Add(cookie);
                }

                var handler = new HttpClientHandler { CookieContainer = cookieContainer, UseCookies = true };
                using var client = new HttpClient(handler);
                var request = new HttpRequestMessage(HttpMethod.Get, downloadInfo.DirectLink);

                // Chỉ đặt các header cốt lõi một cách tường minh.
                request.Headers.UserAgent.ParseAdd(downloadInfo.UserAgent);
                request.Headers.Referrer = new Uri(downloadInfo.RefererUrl);

                if (Request.Headers.TryGetValue("Range", out var rangeHeader))
                {
                    request.Headers.Add("Range", rangeHeader.ToString());
                }

                var fshareResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);

                Response.StatusCode = (int)fshareResponse.StatusCode;

                var allowedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Content-Length", "Content-Type", "Content-Range",
                "Accept-Ranges", "ETag", "Last-Modified", "Date"
            };

                foreach (var header in fshareResponse.Headers.Concat(fshareResponse.Content.Headers))
                {
                    if (allowedHeaders.Contains(header.Key))
                    {
                        Response.Headers[header.Key] = header.Value.ToArray();
                    }
                }

                // Luôn tự tạo Content-Disposition để đảm bảo mã hóa chính xác
                var fileName = downloadInfo.FileName;
                var sanitizedFileNameFallback = Regex.Replace(fileName, @"[^\u0020-\u007E]", "_");
                Response.Headers["Content-Disposition"] = $"attachment; filename=\"{sanitizedFileNameFallback}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";

                var fshareStream = await fshareResponse.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
                await fshareStream.CopyToAsync(Response.Body, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                //_logger.LogInformation("Stream copy interrupted for file '{FileName}'. Exception: {ExceptionType}", downloadInfo.FileName, ex.GetType().Name);
            }
        }
    }

}

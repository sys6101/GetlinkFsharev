using GetlinkFshare.Models;
using PuppeteerSharp;
using System.Net;

namespace GetlinkFshare.Services
{
    public class PuppeteerService : IAsyncDisposable
    {
        private readonly IBrowser _browser;
        private readonly IConfiguration _configuration;
        private readonly ILogger<PuppeteerService> _logger;

        public PuppeteerService(IConfiguration configuration, ILogger<PuppeteerService> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _logger.LogInformation("Đang khởi tạo PuppeteerService...");

            var browserInitializationTask = InitializeBrowserAsync();
            browserInitializationTask.Wait();
            _browser = browserInitializationTask.Result;

            LoginToTargetSite().Wait();
        }

        private async Task<IBrowser> InitializeBrowserAsync()
        {
            _logger.LogInformation("--> Đang tải trình duyệt (nếu cần)...");
            await new BrowserFetcher().DownloadAsync();
            _logger.LogInformation("--> Tải trình duyệt hoàn tất.");

            var launchOptions = new LaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage", "--disable-gpu" }
            };

            _logger.LogInformation("--> Đang khởi chạy trình duyệt...");
            var browser = await Puppeteer.LaunchAsync(launchOptions);

            var client = await browser.Target.CreateCDPSessionAsync();
            await client.SendAsync("Browser.setDownloadBehavior", new { behavior = "deny" });

            _logger.LogInformation("--> Hành vi download mặc định đã được đặt thành 'deny'.");
            _logger.LogInformation("--> Khởi chạy trình duyệt thành công!");
            return browser;
        }

        private async Task LoginToTargetSite()
        {
            var page = await _browser.NewPageAsync();
            try
            {
                var credentials = _configuration.GetSection("TargetWebsite");
                await page.GoToAsync(credentials["LoginUrl"], new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle2 } });

                await page.TypeAsync("#loginform-email", credentials["Username"]);
                await page.TypeAsync("#loginform-password", credentials["Password"]);
                await page.ClickAsync("#loginform-rememberme");
                await page.ClickAsync("form#form-signup button[type='submit']");

                await page.WaitForSelectorAsync("div.user__profile", new WaitForSelectorOptions { Timeout = 15000 });
                _logger.LogInformation("Đăng nhập vào Fshare.vn THÀNH CÔNG!");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LỖI nghiêm trọng khi đăng nhập vào Fshare.vn.");
                throw;
            }
            finally
            {
                await page.CloseAsync();
            }
        }

        public async Task<DownloadInfo?> GetDownloadInfoAsync(string fshareUrl, string? filePassword = null)
        {
            var page = await _browser.NewPageAsync();
            try
            {
                _logger.LogInformation("Đang xử lý Fshare URL: {fshareUrl}", fshareUrl);

                var tcs = new TaskCompletionSource<(string Url, long? FileSize)>();

                page.Response += (sender, e) =>
                {
                    if (e.Response.Headers.TryGetValue("content-disposition", out var contentDisposition) &&
                        contentDisposition.Contains("attachment"))
                    {
                        long? fileSize = null;
                        if (e.Response.Headers.TryGetValue("content-length", out var lengthStr) &&
                            long.TryParse(lengthStr, out var length))
                        {
                            fileSize = length;
                        }
                        tcs.TrySetResult((e.Response.Url, fileSize));
                    }
                };

        // Mở link Fshare
        var navigationTask = page.GoToAsync(fshareUrl, new NavigationOptions
        {
            WaitUntil = new[] { WaitUntilNavigation.Networkidle2 },
            Timeout = 10000
        });

        var completedTask = await Task.WhenAny(navigationTask, tcs.Task);

        if (completedTask == tcs.Task)
        {
            _logger.LogInformation("Đã bắt được link download qua redirect.");
        }
        else
        {
            // Trường hợp cần nhập mật khẩu
            const string passwordInputSelector = "#downloadpasswordform-password";
            var passwordInput = await page.QuerySelectorAsync(passwordInputSelector);

            if (passwordInput != null)
            {
                if (string.IsNullOrEmpty(filePassword))
                {
                    throw new Exception("File được bảo vệ bằng mật khẩu. Vui lòng cung cấp mật khẩu.");
                }

                // Xóa thông báo lỗi cũ (nếu có)
                const string errorSelector = "p.mdc-textfield-helptext--validation-msg";
                var oldError = await page.QuerySelectorAsync(errorSelector);
                if (oldError != null)
                {
                    await page.EvaluateFunctionAsync("el => el.remove()", oldError);
                }

                // Nhập mật khẩu và submit
                await passwordInput.TypeAsync(filePassword);
                const string submitButtonSelector = "button.mdc-button--raised[type='submit']";
                await page.ClickAsync(submitButtonSelector);

                // Chờ hoặc link, hoặc báo lỗi
                var errorTask = page.WaitForSelectorAsync(errorSelector,
                    new WaitForSelectorOptions { Timeout = 7000 });
                var passwordCompletedTask = await Task.WhenAny(tcs.Task, errorTask);

                if (passwordCompletedTask == errorTask && errorTask.Result != null)
                {
                    throw new Exception("Mật khẩu file không đúng hoặc không được để trống");
                }
            }

            // Nếu vẫn chưa có link → click nút TẢI NHANH
            if (!tcs.Task.IsCompleted)
            {
                _logger.LogInformation("Không có link redirect, thử click nút TẢI NHANH...");
                var button = await page.QuerySelectorAsync("button.btn_download_vip");
                if (button != null)
                {
                    await button.ClickAsync();

                    var clickCompleted = await Task.WhenAny(tcs.Task, Task.Delay(15000));
                    if (clickCompleted != tcs.Task)
                    {
                        _logger.LogWarning("Không bắt được link sau khi click nút tải.");
                    }
                }
                else
                {
                    _logger.LogWarning("Không tìm thấy nút TẢI NHANH trên trang.");
                }
            }
        }

        // Cho tác vụ bắt link thêm 1 cơ hội nữa để hoàn thành
        if (!tcs.Task.IsCompleted)
        {
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(6));
        }

        var (directLink, fileSize) = await tcs.Task;

        if (string.IsNullOrEmpty(directLink)) return null;

        var allCookies = await page.GetCookiesAsync(fshareUrl, directLink);
        var fshareCookies = allCookies.Where(c => c.Domain == ".fshare.vn").ToArray();

        var fileName = WebUtility.UrlDecode(Path.GetFileName(new Uri(directLink).AbsolutePath));
        var userAgent = await _browser.GetUserAgentAsync();
        var refererUrl = fshareUrl;

        return new DownloadInfo
        {
            OriginalFshareUrl = fshareUrl,
            DirectLink = directLink,
            Cookies = fshareCookies,
            FileSize = fileSize,
            UserAgent = userAgent,
            RefererUrl = refererUrl,
            FileName = fileName
        };
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Lỗi khi lấy thông tin download cho {fshareUrl}.", fshareUrl);
        throw;
    }
    finally
    {
        if (!page.IsClosed)
        {
            await page.CloseAsync();
        }
    }
}

        public async Task<StorageInfo?> GetStorageInfoAsync()
        {
            var page = await _browser.NewPageAsync();
            try
            {
                const string infoUrl = "https://www.fshare.vn/account/inforesource";
                _logger.LogInformation("Đang truy cập trang thông tin dung lượng: {url}", infoUrl);
                await page.GoToAsync(infoUrl, new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Networkidle2 } });

                const string storageSelector = "div#down-traffic-profile div.account-storage";
                _logger.LogInformation("Đang chờ selector: {selector}", storageSelector);
                var storageDiv = await page.WaitForSelectorAsync(storageSelector, new WaitForSelectorOptions { Timeout = 15000 });

                if (storageDiv == null)
                {
                    _logger.LogWarning("Không thể tìm thấy div chứa thông tin dung lượng.");
                    return null;
                }

                var spanElements = await storageDiv.QuerySelectorAllAsync("span");

                if (spanElements.Length >= 2)
                {
                    var usedStorageHandle = await spanElements[0].GetPropertyAsync("innerText");
                    var usedStorage = await usedStorageHandle.JsonValueAsync<string>();

                    var availableTodayHandle = await spanElements[1].GetPropertyAsync("innerText");
                    var availableToday = await availableTodayHandle.JsonValueAsync<string>();

                    return new StorageInfo
                    {
                        UsedStorage = usedStorage?.Trim() ?? string.Empty,
                        AvailableToday = availableToday?.Trim() ?? string.Empty,
                        //Giá trị ngày giờ sẽ được cập nhật trong worker
                        LastUpdated = null

                    };
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy thông tin dung lượng Fshare.");
                await page.ScreenshotAsync($"./storage_info_error_{DateTime.Now:yyyyMMddHHmmss}.png");
                return null;
            }
            finally
            {
                if (!page.IsClosed)
                {
                    await page.CloseAsync();
                }
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (_browser != null && !_browser.IsClosed)
            {
                await _browser.CloseAsync();
            }
        }
    }


}

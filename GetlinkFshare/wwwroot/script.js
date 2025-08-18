$(document).ready(function () {
    // URL của API, bạn cần thay đổi <port> cho đúng với project của bạn
    const API_BASE_URL = ""; // <<-- THAY ĐỔI PORT Ở ĐÂY
    const TOKEN_KEY = 'fshareApiToken'; // Khóa để lưu token trong localStorage

    let signalRConnection = null;

    // Hàm kiểm tra trạng thái đăng nhập và cập nhật giao diện
    function checkLoginState() {
        const storedToken = localStorage.getItem(TOKEN_KEY);
        if (storedToken) {
            // Nếu có token, ẩn form đăng nhập và hiện khu vực làm việc
            $('#login-section').hide();
            $('#scraping-section').show();
            if (signalRConnection == null) {
                startSignalRConnection(storedToken);
            }
        } else {
            // Nếu không có token, hiện form đăng nhập
            $('#login-section').show();
            $('#scraping-section').hide();
        }
    }
    function startSignalRConnection(jwtToken) {
        if (!jwtToken) return;

        signalRConnection = new signalR.HubConnectionBuilder()
            .withUrl("/fshareInfoHub", {
                accessTokenFactory: () => jwtToken
            })
            .withAutomaticReconnect()
            .build();

        // *** ĐÃ CẬP NHẬT: Cập nhật giao diện mới ***
        signalRConnection.on("ReceiveStorageInfo", function (info) {
            console.log("Received storage info:", info);
            const { usedStorage, availableToday, lastUpdated } = info;

            const updatedTime = new Date(lastUpdated).toLocaleTimeString('vi-VN');

            // Cập nhật các phần tử mới
            $('#storage-used').text(usedStorage.replace('Sử dụng: ', ''));
            $('#storage-available').text(availableToday.replace('Còn khả dụng: ', ''));
            $('#storage-updated').text(updatedTime);
        });

        signalRConnection.start().then(function () {
            console.log("SignalR Connected (Authenticated).");
        }).catch(function (err) {
            console.error(err.toString());
        });
    }

    // Hàm format kích thước file cho dễ đọc
    function formatBytes(bytes, decimals = 2) {
        if (!+bytes) return '0 Bytes';
        const k = 1024;
        const dm = decimals < 0 ? 0 : decimals;
        const sizes = ['Bytes', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return `${parseFloat((bytes / Math.pow(k, i)).toFixed(dm))} ${sizes[i]}`;
    }

    // Xử lý form đăng nhập
    $('#login-form').on('submit', function (e) {
        e.preventDefault();
        const username = $('#username').val();
        const password = $('#password').val();
        const $messageDiv = $('#login-message');
        $messageDiv.html('<span class="text-blue-500">Đang đăng nhập...</span>');

        $.ajax({
            url: `${API_BASE_URL}/api/auth/login`,
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ username: username, password: password }),
            success: function (response) {
                // *** ĐÃ THAY ĐỔI: Lưu token vào localStorage ***
                localStorage.setItem(TOKEN_KEY, response.token);
                // Cập nhật lại giao diện
                checkLoginState();
            },
            error: function (jqXHR) {
                const errorMsg = jqXHR.responseText || "Lỗi không xác định.";
                $messageDiv.html(`<span class="text-red-500">Lỗi: ${errorMsg}</span>`);
            }
        });
    });

    // Xử lý form scraping
    $('#scraping-form').on('submit', function (e) {
        e.preventDefault();
        const fshareUrl = $('#fshareUrl').val();
        //thêm password file nếu có
        const filePassword = $('#filePassword').val();
        const $resultDiv = $('#scraping-result');
        const $button = $(this).find('button[type="submit"]');

        // *** ĐÃ THAY ĐỔI: Lấy token từ localStorage để sử dụng ***
        const jwtToken = localStorage.getItem(TOKEN_KEY);

        if (!jwtToken) {
            alert("Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.");
            if (signalRConnection) {
                signalRConnection.stop().then(() => {
                    console.log("SignalR Disconnected.");
                    signalRConnection = null;
                });
            }
            checkLoginState(); // Cập nhật giao diện về trang đăng nhập
            return;
        }
        if (!fshareUrl) {
            alert("Vui lòng nhập URL Fshare.");
            return;
        }
        const requestData = {
            fshareUrl: fshareUrl
        };
        if (filePassword) {
            requestData.filePassword = filePassword;
        }

        $button.prop('disabled', true);
        $button.find('.button-text').text('Đang xử lý...');
        $button.find('.loader').removeClass('hidden');
        $resultDiv.addClass('hidden').html('');
        

        $.ajax({
            url: `${API_BASE_URL}/api/scraping/prepare-download`,
            method: 'GET',
            data: requestData,
            beforeSend: function (xhr) {
                xhr.setRequestHeader('Authorization', `Bearer ${jwtToken}`);
            },
            success: function (response) {
                const { proxyUrl, fileName, fileSize } = response;
                const formattedSize = fileSize ? formatBytes(fileSize) : 'Không rõ';

                const resultHtml = `
                    <h3 class="font-bold mb-2 text-gray-800">Link tải đã sẵn sàng!</h3>
                    <div class="text-sm text-gray-600">
                        <p><strong>Tên file:</strong> ${fileName || 'Không rõ'}</p>
                        <p><strong>Dung lượng:</strong> ${formattedSize}</p>
                    </div>
                    <a href="${proxyUrl}" download="${fileName || ''}" class="mt-4 inline-block w-full text-center bg-green-500 hover:bg-green-700 text-white font-bold py-2 px-4 rounded focus:outline-none focus:shadow-outline">
                        Tải Xuống Ngay
                    </a>
                    <p class="text-xs text-gray-500 mt-2">Lưu ý: Link tải chỉ có hiệu lực trong 10 phút.</p>
                `;
                $resultDiv.html(resultHtml).removeClass('hidden').removeClass('border-red-300').addClass('border-gray-200');
            },
            error: function (jqXHR) {
                // Nếu lỗi là 401 Unauthorized, có thể token đã hết hạn trên server
                if (jqXHR.status === 401) {
                    alert("Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.");
                    localStorage.removeItem(TOKEN_KEY); // Xóa token cũ
                    // Ngắt kết nối SignalR nếu đang kết nối
                    if (signalRConnection) {
                        signalRConnection.stop().then(() => {
                            console.log("SignalR Disconnected.");
                            signalRConnection = null;
                        });
                    }
                    checkLoginState(); // Cập nhật giao diện
                } else {
                    const errorMsg = jqXHR.responseText || "Lỗi không xác định.";
                    const errorHtml = `
                        <h3 class="font-bold mb-2 text-red-700">Đã xảy ra lỗi</h3>
                        <pre class="text-red-800 whitespace-pre-wrap break-all">${errorMsg}</pre>
                    `;
                    $resultDiv.html(errorHtml).removeClass('hidden').removeClass('border-gray-200').addClass('border-red-300');
                }
            },
            complete: function () {
                $button.prop('disabled', false);
                $button.find('.button-text').text('Chuẩn bị Link Tải');
                $button.find('.loader').addClass('hidden');
            }
        });
    });

    // Xử lý nút đăng xuất
    $('#logout-button').on('click', function () {
        // *** ĐÃ THAY ĐỔI: Xóa token khỏi localStorage ***
        localStorage.removeItem(TOKEN_KEY);
        // Ngắt kết nối SignalR nếu đang kết nối
        if (signalRConnection) {
            signalRConnection.stop().then(() => {
                console.log("SignalR Disconnected.");
                signalRConnection = null;
            });
        }
        // Cập nhật giao diện
        checkLoginState();
        // Dọn dẹp các ô input và kết quả cũ
        $('#scraping-result').addClass('hidden').html('');
        $('#fshareUrl').val('');
        $('#filePassword').val('');
        $('#login-message').html('');
    });

    // *** ĐÃ THÊM: Kiểm tra trạng thái đăng nhập ngay khi trang được tải ***
    checkLoginState();
});
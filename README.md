# Concert Ticket (PRN212)

App WPF đặt vé concert: đăng nhập Google, chọn vé theo khu, thanh toán VNPay Sandbox, vé (QR trên Cloudinary) tự vào Google Calendar.

## Chạy lần đầu

1. **Database:** mở `Database/ConcertTicketDB.sql` trong SSMS và chạy (chạy lại sẽ xóa và tạo mới database).
2. **Chuỗi kết nối:** mặc định dùng Windows Authentication (`Trusted_Connection=True`). Máy nào dùng `sa` thì ghi đè trong `appsettings.Development.json` (bước 3).
3. **Key bí mật:** tạo file `ConcertTicket/appsettings.Development.json` (file này đã nằm trong `.gitignore`, KHÔNG push; xin key qua tin nhắn riêng). Không sửa `appsettings.json`. App tự đọc file này khi chạy Debug, giá trị trong đây đè lên `appsettings.json`:

   ```json
   {
     "Google": { "ClientId": "...", "ClientSecret": "..." },
     "VnPay": { "TmnCode": "...", "HashSecret": "..." },
     "Cloudinary": { "CloudName": "...", "ApiKey": "...", "ApiSecret": "..." }
   }
   ```

   Máy dùng `sa` thì thêm vào cùng file:

   ```json
   "ConnectionStrings": { "ConcertDb": "Server=(local);uid=sa;pwd=...;database=ConcertTicketDB;TrustServerCertificate=True" }
   ```

4. Mở `ConcertTicket.slnx` bằng Visual Studio, bấm Run, bấm **"Đăng nhập bằng Google"**. Email Google phải nằm trong danh sách Test users của OAuth app (nhờ Huy thêm), nếu không Google báo `403: access_denied`.

## Google Calendar (F6): setup và sử dụng

1. Trong [Google Cloud Console](https://console.cloud.google.com/), chọn cùng project đang dùng cho đăng nhập Google. Vào **APIs & Services → Library**, tìm **Google Calendar API**, bấm **Enable**.
2. Trong **Google Auth Platform**, cấu hình màn hình đồng ý truy cập. Nếu app đang ở trạng thái **Testing**, thêm email dùng demo vào **Audience → Test users**. Khai báo scope `https://www.googleapis.com/auth/calendar.events` trong **Data Access**.
3. Trong **Clients**, tạo OAuth client loại **Desktop app** nếu nhóm chưa có. Điền `ClientId` và `ClientSecret` vào mục `Google` trong `ConcertTicket/appsettings.Development.json` theo mẫu bên trên. Calendar dùng chung OAuth với đăng nhập, không cần API key hay service account riêng.
4. Đặt **ConcertTicket** làm startup project trong Visual Studio rồi chạy. Chấp nhận quyền truy cập Calendar khi đăng nhập. Nếu từng đăng nhập trước khi thêm scope, **Đăng xuất** rồi đăng nhập lại để cấp quyền.
5. Mở màn vé của một đơn **Paid** thuộc tài khoản đang đăng nhập. App tải vé đã phát hành; nếu chưa đủ vé thì gọi `ITicketService.IssueTicketsAsync`. Sau khi đủ vé, app tự thêm một sự kiện vào lịch **primary** của tài khoản đó.
6. Khi hiện **"Đã thêm lịch, nhắc trước 1 ngày."**, bấm **Mở Google Calendar**. Sự kiện chứa tên concert, giờ bắt đầu/kết thúc, địa điểm, khu, cổng, từng mã vé và URL ảnh QR. Giờ concert trong DB được hiểu là giờ Việt Nam (`Asia/Ho_Chi_Minh`, UTC+7).
7. Nếu thêm lịch lỗi, vé vẫn hiển thị; bấm **Thử lại Calendar** sau khi khắc phục. Mở lại màn vé hoặc thử lại không tạo thêm sự kiện cho cùng đơn. Lỗi Calendar không thay đổi trạng thái thanh toán.

**Điều kiện tích hợp:** `VnPayService` và `TicketService` hiện vẫn còn `TODO` của phần thanh toán/phát hành vé. Luồng mua mới chỉ chạy đến Calendar sau khi hai phần này hoàn thành. F6 dùng được với đơn `Paid` đã có đủ vé trong DB; không cần sửa schema.

### Code liên quan

- [`ICalendarService.AddOrderEventAsync(int orderId)`](ConcertTicket/Services/Interfaces/ICalendarService.cs): giữ nguyên interface, trả link sự kiện.
- [`GoogleCalendarService`](ConcertTicket/Services/GoogleCalendarService.cs): kiểm tra đăng nhập, chủ đơn, trạng thái `Paid` và đủ vé; lấy `Zone.Concert` cùng `Tickets`; tạo sự kiện với nhắc popup trước 1.440 phút; lưu `Order.GoogleEventId` và `Order.GoogleEventLink`.
- ID sự kiện được tạo ổn định từ tài khoản, mã đơn và thời điểm tạo đơn. Nếu Google đã nhận yêu cầu nhưng DB lưu lỗi, lần thử lại nhận HTTP `409` rồi lấy lại đúng sự kiện, không phát sinh thêm bản sao. Đơn đã có ID/link hợp lệ trả luôn link đã lưu.
- [`TicketWindow`](ConcertTicket/Windows/TicketWindow.xaml.cs): gọi Calendar sau khi có đủ vé, khóa nút khi chờ, hiện trạng thái và cho thử lại khi lỗi. Các lỗi thêm lịch không chặn việc xem vé.
- [`GoogleAuthService`](ConcertTicket/Services/GoogleAuthService.cs) đã xin scope `calendar.events`; credential dùng lại từ `IUserSession.GoogleCredential`. DI đã đăng ký trong `App.xaml.cs`.

F6 chỉ tạo sự kiện và lưu liên kết; không đồng bộ việc sửa/xóa sự kiện thủ công trong Google Calendar. Nhắc trước một ngày được gửi dưới dạng popup; việc hiện thông báo phụ thuộc cài đặt Calendar/trình duyệt của người dùng.

### Tự chạy test

Yêu cầu: Windows, .NET SDK 10, SQL Server Express **LocalDB** có instance `MSSQLLocalDB` (kiểm tra bằng `sqllocaldb info MSSQLLocalDB`). Chạy từ thư mục chứa solution:

```powershell
dotnet build ConcertTicket.slnx
dotnet run --project ConcertTicket.Tests
```

Test dùng SQL Server LocalDB thật với database tạm tên `ConcertTicketCalendarTests_<GUID>`, tự xóa trong `finally`. Không chạy script reset DB của app. Google HTTP được giả lập ngay trong test, không dùng key/token thật và không tạo sự kiện trong tài khoản Google.

Các ca kiểm tra gồm nội dung sự kiện, UTC+7, nhắc trước một ngày, chủ đơn/quyền truy cập, đơn chưa thanh toán hoặc thiếu vé, chống trùng khi gọi lại/đồng thời, khôi phục sau lỗi lưu DB, chặn link không an toàn, màn WPF khi đang chờ/thành công/lỗi và nút thử lại. Ảnh kiểm tra giao diện `calendar-success.png` và `calendar-retry.png` nằm trong thư mục output của `ConcertTicket.Tests`.

### Xử lý lỗi khi demo thật

| Hiện tượng | Cách xử lý |
| --- | --- |
| `403: access_denied` khi đăng nhập | Thêm đúng email vào Test users của OAuth project. |
| Không có quyền Calendar / HTTP 401, 403 | Bật Google Calendar API đúng project, kiểm tra scope, đăng xuất rồi đăng nhập lại và cấp quyền. |
| Chưa phát hành đủ vé / `TODO (Bảo): IssueTicketsAsync` | Hoàn thành F5 hoặc dùng đơn đã phát hành đủ vé để demo F6. |
| Mất mạng hoặc quá thời gian chờ | Khôi phục mạng rồi bấm **Thử lại Calendar**. Yêu cầu thêm lịch có giới hạn chờ 30 giây. |
| Google đã có sự kiện nhưng app báo lỗi lưu DB | Kiểm tra kết nối SQL Server rồi thử lại; app khôi phục liên kết tới sự kiện đã tạo. |
| Bấm mở lịch nhưng không thấy sự kiện | Kiểm tra tài khoản đang đăng nhập trên trình duyệt có trùng tài khoản trong app. |

## Cấu trúc

| Thư mục | Nội dung | Ai làm |
| --- | --- | --- |
| `App.xaml.cs` | Composition root: đăng ký Options, DbContextFactory, Service, Window | Huy |
| `Options/` | GoogleOptions, VnPayOptions, CloudinaryOptions | Huy |
| `Models/` | Scaffold từ database (đừng sửa tay; thêm code thì viết file `*.Extra.cs` như `Zone.Extra.cs`) | Huy |
| `Services/Interfaces/` | 6 Interface đã chốt | Cả nhóm |
| `Services/` | UserSession, NavigationService, GoogleAuthService (Huy) · VnPayService (Dương) · TicketService, GoogleCalendarService (Bảo) | |
| `Windows/` | LoginWindow (Huy) · ConcertDetailWindow, PaymentWindow (Dương) · MainWindow, TicketWindow (Bảo) | |

Tìm `TODO (Tên)` để thấy phần việc của mình.

## Quy ước

1. Không `new` Window hay Service. Cần gì thì khai trong constructor; mở Window bằng `_nav.Show...()`.
2. Không giữ DbContext làm biến của Window: `using var ctx = _dbFactory.CreateDbContext();` mỗi lần dùng.
3. Key chỉ đọc qua `IOptions<T>` trong Service, và đọc `.Value` bên trong hàm (không đọc trong constructor).
4. Thêm class mới thì đăng ký trong `App.xaml.cs`.
5. Hàm gọi Google/Cloudinary là `...Async`: thêm `async` vào hàm Click, `await` trước lời gọi, khóa nút trong lúc chờ, bọc `try/catch`.

## Scaffold lại khi đổi database

```
dotnet ef dbcontext scaffold "Server=(local);Database=ConcertTicketDB;Trusted_Connection=True;TrustServerCertificate=True" Microsoft.EntityFrameworkCore.SqlServer --output-dir Models --context ConcertDbContext --no-onconfiguring --force
```

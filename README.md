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

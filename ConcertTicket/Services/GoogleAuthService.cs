using ConcertTicket.Models;
using ConcertTicket.Options;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConcertTicket.Services;

//Người viết: Huy (F1)
public class GoogleAuthService : IAuthService
{
    //giữ IOptions, chỉ đọc .Value bên trong hàm:
    //nếu đọc trong constructor thì máy chưa có key sẽ lỗi ngay lúc mở app
    private readonly IOptions<GoogleOptions> _options;
    private readonly IUserSession _session;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly ILogger<GoogleAuthService> _logger;

    public GoogleAuthService(IOptions<GoogleOptions> options, IUserSession session,
                             IDbContextFactory<ConcertDbContext> dbFactory, ILogger<GoogleAuthService> logger)
    {
        _options = options;
        _session = session;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public Task<User?> LoginWithGoogleAsync()
    {
        //TODO (Huy):
        //1. GoogleOptions google = _options.Value;
        //2. GoogleWebAuthorizationBroker.AuthorizeAsync(...) với scope openid, email, profile, calendar.events
        //3. đọc email, tên, avatar, sub từ id token
        //4. tìm User theo GoogleSub, chưa có thì tạo mới (using var ctx = _dbFactory.CreateDbContext())
        //5. _session.CurrentUser = user; _session.GoogleCredential = credential;
        throw new NotImplementedException("TODO (Huy): đăng nhập Google chưa làm. Tạm dùng nút 'Vào thử'.");
    }

    public Task LogoutAsync()
    {
        //TODO (Huy): xóa token Google đã lưu trên máy (FileDataStore)
        _session.Clear();
        return Task.CompletedTask;
    }
}

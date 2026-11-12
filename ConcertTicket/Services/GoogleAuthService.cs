using ConcertTicket.Models;
using ConcertTicket.Options;
using ConcertTicket.Services.Interfaces;
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Util.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConcertTicket.Services;

public class GoogleAuthService : IAuthService
{
    //giữ IOptions, chỉ đọc .Value bên trong hàm:
    //nếu đọc trong constructor thì máy chưa có key sẽ lỗi ngay lúc mở app
    private readonly IOptions<GoogleOptions> _options;
    private readonly IUserSession _session;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly ILogger<GoogleAuthService> _logger;

    //token google lưu lại trên máy, để lần sau mở app không cần login lại
    private const string TokenFolder = "ConcertTicket.GoogleToken";

    //xin luông quyền truy cập email, profile, calendar.events, chỉ cần đống ý 1 lần
    private static readonly string[] Scopes =
    {
        "openid", "email", "profile", 
        CalendarService.Scope.CalendarEvents
    };

    public GoogleAuthService(IOptions<GoogleOptions> options, IUserSession session,
                             IDbContextFactory<ConcertDbContext> dbFactory, ILogger<GoogleAuthService> logger)
    {
        _options = options;
        _session = session;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<User?> LoginWithGoogleAsync()
    {
        //1. GoogleOptions google = _options.Value; đọc key
        GoogleOptions google = _options.Value;
        var secrets = new ClientSecrets
        {
            ClientId = google.ClientId,
            ClientSecret = google.ClientSecret
        };

        //2. GoogleWebAuthorizationBroker.AuthorizeAsync(...) với scope openid, email, profile, calendar.events
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        UserCredential credential;
        try
        {
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets, Scopes, "user", cts.Token,
                new FileDataStore(TokenFolder));
        }
        catch(OperationCanceledException)
        {
            return null; //hết 2 phút, user không login
        }
        catch(TokenResponseException ex) when (ex.Error?.Error == "access_denied")
        {
            return null; //user bấm Cancel trên trang Google
        }
        //lỗi khác (sai key, mất mạng...) không bắt ở đây => LoginWindow hiện "Đăng nhập thất bại: ..."

        //token cũ lưu trên máy đã hết hạn => xin token mới
        if (credential.Token.IsStale)
        {
            await credential.RefreshTokenAsync(CancellationToken.None);
        }

        //3. đọc email, tên, avatar, sub từ id token
        var payload = await GoogleJsonWebSignature.ValidateAsync(credential.Token.IdToken, 
            new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { google.ClientId } });

        //4. tìm User theo GoogleSub, chưa có thì tạo mới (using var ctx = _dbFactory.CreateDbContext())
        using var ctx = await _dbFactory.CreateDbContextAsync();
        User? user = await ctx.Users.FirstOrDefaultAsync(u => u.GoogleSub == payload.Subject);
        if (user == null)
        {
            user = new User { GoogleSub = payload.Subject, CreatedAt = DateTime.Now };
            ctx.Users.Add(user);
        }
        user.Email = payload.Email;
        user.FullName = payload.Name ?? payload.Email;
        user.AvatarUrl = payload.Picture;
        await ctx.SaveChangesAsync();

        //5. _session.CurrentUser = user; _session.GoogleCredential = credential;
        _session.CurrentUser = user;
        _session.GoogleCredential = credential; //dùng cái này để gọi Calendar
        _logger.LogInformation("User {Email} logged in with Google", user.Email);
        return user;
    }

    public async Task LogoutAsync()
    {
        //TODO (Huy): xóa token Google đã lưu trên máy (FileDataStore)
        await new FileDataStore(TokenFolder).ClearAsync(); //xóa token trên máy => lần sau đăng nhập lại
        _session.Clear();
    }
}

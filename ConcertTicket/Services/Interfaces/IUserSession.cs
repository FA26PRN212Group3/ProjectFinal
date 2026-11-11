using ConcertTicket.Models;
using Google.Apis.Auth.OAuth2;

namespace ConcertTicket.Services.Interfaces;

//nhớ ai đang đăng nhập, mọi Window đều đọc được (đăng ký Singleton)
public interface IUserSession
{
    User? CurrentUser { get; set; }

    //token Google, đã có quyền Calendar (Huy gán lúc login, Bảo dùng để gọi Calendar API)
    UserCredential? GoogleCredential { get; set; }

    bool IsLoggedIn { get; }   //CurrentUser != null

    void Clear();              //đăng xuất
}

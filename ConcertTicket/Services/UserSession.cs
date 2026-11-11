using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;
using Google.Apis.Auth.OAuth2;

namespace ConcertTicket.Services;

//Người viết: Huy
//đăng ký Singleton => cả app dùng chung 1 object này
public class UserSession : IUserSession
{
    public User? CurrentUser { get; set; }

    public UserCredential? GoogleCredential { get; set; }

    public bool IsLoggedIn => CurrentUser != null;

    public void Clear()
    {
        CurrentUser = null;
        GoogleCredential = null;
    }
}

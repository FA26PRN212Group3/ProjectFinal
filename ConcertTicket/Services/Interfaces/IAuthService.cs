using ConcertTicket.Models;

namespace ConcertTicket.Services.Interfaces;

//Người viết: Huy
public interface IAuthService
{
    //mở trình duyệt cho user chọn tài khoản Google (scope: openid, email, profile, calendar.events)
    //tìm User theo GoogleSub, chưa có thì tạo mới; lưu vào IUserSession
    //trả về User nếu thành công, null nếu user đóng trình duyệt / bấm Cancel
    Task<User?> LoginWithGoogleAsync();

    //xóa token Google đã lưu trên máy + IUserSession.Clear()
    Task LogoutAsync();
}

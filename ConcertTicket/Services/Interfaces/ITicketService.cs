using ConcertTicket.Models;

namespace ConcertTicket.Services.Interfaces;

//Người viết: Bảo
public interface ITicketService
{
    //tạo Quantity vé cho đơn Paid: mã vé, gate theo khu, ảnh QR upload Cloudinary
    //gọi lại lần 2 không tạo trùng: đơn đã có vé thì trả vé cũ
    Task<List<Ticket>> IssueTicketsAsync(int orderId);

    //CON-yyyyMMdd-XXXXXX, kiểm tra không trùng trong DB
    string GenerateTicketCode(DateTime concertDate);
}

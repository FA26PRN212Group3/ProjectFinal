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

    //Người viết: Huy (check-in)
    //soát vé: Valid -> Used. Result = "OK" (vào cổng), "Used" (đã check-in rồi), "Invalid" (mã lạ)
    Task<CheckInResult> CheckInAsync(string ticketCode);

    //đọc mã QR trong file ảnh, không thấy QR thì trả null
    string? ReadQrCode(string imagePath);
}

public class CheckInResult
{
    public string Result { get; set; } = "Invalid";

    public Ticket? Ticket { get; set; }   //có Order.Zone.Concert để hiện tên concert, khu, cổng
}

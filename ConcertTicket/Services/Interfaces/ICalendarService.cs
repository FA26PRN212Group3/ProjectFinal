namespace ConcertTicket.Services.Interfaces;

//Người viết: Bảo
public interface ICalendarService
{
    //tạo 1 sự kiện trên lịch primary của user đang đăng nhập cho cả đơn:
    //  tiêu đề = tên concert, giờ = StartTime..EndTime, địa điểm = Venue + Address,
    //  mô tả = khu, gate, từng mã vé + link ảnh, nhắc trước 1 ngày
    //lưu GoogleEventId + GoogleEventLink vào Order; đơn đã có sự kiện thì không tạo lại
    //trả về GoogleEventLink để TicketWindow mở
    Task<string> AddOrderEventAsync(int orderId);

    //xóa sự kiện khi tất cả vé đã check-in, giữ ID/link để không tạo lại lịch
    Task RemoveOrderEventAsync(int orderId);
}

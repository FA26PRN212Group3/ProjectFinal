using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConcertTicket.Services;

//Người viết: Bảo (F6)
public class GoogleCalendarService : ICalendarService
{
    private readonly IUserSession _session;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly ILogger<GoogleCalendarService> _logger;

    public GoogleCalendarService(IUserSession session, IDbContextFactory<ConcertDbContext> dbFactory,
                                 ILogger<GoogleCalendarService> logger)
    {
        _session = session;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public Task<string> AddOrderEventAsync(int orderId)
    {
        //TODO (Bảo):
        //1. lấy Order (Include Zone.Concert, Tickets); đã có GoogleEventId thì trả GoogleEventLink luôn
        //2. new CalendarService(new BaseClientService.Initializer { HttpClientInitializer = _session.GoogleCredential })
        //3. Events.Insert(event, "primary"): tiêu đề, giờ, địa điểm, mô tả (mã vé + link ảnh), nhắc trước 1 ngày
        //4. lưu GoogleEventId + GoogleEventLink (htmlLink) vào Order
        throw new NotImplementedException("TODO (Bảo): AddOrderEventAsync");
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;
using Google;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
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

    public async Task<string> AddOrderEventAsync(int orderId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(orderId);
        var user = _session.CurrentUser ?? throw new InvalidOperationException("Bạn cần đăng nhập Google trước.");
        var credential = _session.GoogleCredential ?? throw new InvalidOperationException("Bạn cần đăng nhập lại Google để cấp quyền Calendar.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var ctx = await _dbFactory.CreateDbContextAsync(timeout.Token);
        var order = await ctx.Orders
            .Include(o => o.Zone).ThenInclude(z => z.Concert)
            .Include(o => o.Tickets)
            .SingleOrDefaultAsync(o => o.Id == orderId && o.UserId == user.Id, timeout.Token)
            ?? throw new InvalidOperationException("Không tìm thấy đơn hàng của bạn.");

        if (order.Status != "Paid")
            throw new InvalidOperationException("Chỉ thêm lịch cho đơn đã thanh toán.");
        if (order.Tickets.Count == 0 || order.Tickets.Count != order.Quantity)
            throw new InvalidOperationException("Cần phát hành đủ vé trước khi thêm Google Calendar.");
        if (!string.IsNullOrWhiteSpace(order.GoogleEventId) && !string.IsNullOrWhiteSpace(order.GoogleEventLink))
            return RequireCalendarLink(order.GoogleEventLink);

        var concert = order.Zone.Concert;
        if (concert.EndTime <= concert.StartTime)
            throw new InvalidOperationException("Thời gian concert không hợp lệ.");

        // ID cố định tránh tạo trùng khi Google đã tạo sự kiện nhưng DB chưa lưu được.
        var eventId = order.GoogleEventId ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"concert-ticket:{user.GoogleSub}:{order.Id}:{order.CreatedAt:O}"))));
        var calendarEvent = new Event
        {
            Id = eventId,
            Summary = concert.Name,
            Location = $"{concert.Venue}, {concert.Address}",
            Description = WebUtility.HtmlEncode($"Khu: {order.Zone.Name}\nCổng: {order.Zone.Gate}\n" +
                string.Join("\n", order.Tickets.OrderBy(t => t.Id)
                    .Select(t => $"Vé: {t.TicketCode} | Cổng: {t.Gate} | QR: {t.QrImageUrl}"))).Replace("\n", "<br>"),
            // shortcut: DB lưu giờ Việt Nam; thêm timezone vào Concert khi hỗ trợ concert quốc tế.
            Start = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(DateTime.SpecifyKind(concert.StartTime, DateTimeKind.Unspecified), TimeSpan.FromHours(7)),
                TimeZone = "Asia/Ho_Chi_Minh"
            },
            End = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(DateTime.SpecifyKind(concert.EndTime, DateTimeKind.Unspecified), TimeSpan.FromHours(7)),
                TimeZone = "Asia/Ho_Chi_Minh"
            },
            Reminders = new Event.RemindersData
            {
                UseDefault = false,
                Overrides = [new EventReminder { Method = "popup", Minutes = 24 * 60 }]
            }
        };

        using var calendar = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ConcertTicket"
        });
        Event savedEvent;
        try
        {
            savedEvent = await calendar.Events.Insert(calendarEvent, "primary").ExecuteAsync(timeout.Token);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Conflict)
        {
            savedEvent = await calendar.Events.Get("primary", eventId).ExecuteAsync(timeout.Token);
        }

        if (savedEvent.Id != eventId || savedEvent.Status == "cancelled")
            throw new InvalidOperationException("Google Calendar trả về sự kiện không hợp lệ hoặc đã bị xóa.");
        order.GoogleEventLink = RequireCalendarLink(savedEvent.HtmlLink);
        order.GoogleEventId = savedEvent.Id;
        await ctx.SaveChangesAsync(timeout.Token);
        _logger.LogInformation("Đã lưu sự kiện Google Calendar cho đơn {OrderId}", order.Id);
        return order.GoogleEventLink;
    }

    private static string RequireCalendarLink(string? link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            (uri.Host != "www.google.com" && uri.Host != "calendar.google.com"))
            throw new InvalidOperationException("Đường dẫn Google Calendar không hợp lệ.");
        return uri.AbsoluteUri;
    }
}

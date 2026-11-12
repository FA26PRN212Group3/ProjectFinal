using System.Windows;
using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;
using Google;
using Microsoft.EntityFrameworkCore;

namespace ConcertTicket.Windows;

//Người làm: Bảo (F5 + F6 - phát hành vé, Google Calendar)
public partial class TicketWindow : Window
{
    private readonly ITicketService _ticketService;
    private readonly ICalendarService _calendar;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly IUserSession _session;
    private readonly int _orderId;
    private string? _eventLink;

    public TicketWindow(ITicketService ticketService, ICalendarService calendar,
                        IDbContextFactory<ConcertDbContext> dbFactory, IUserSession session, int orderId)
    {
        InitializeComponent();
        _ticketService = ticketService;
        _calendar = calendar;
        _dbFactory = dbFactory;
        _session = session;
        _orderId = orderId;

        Loaded += TicketWindow_Loaded;
    }

    private async void TicketWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= TicketWindow_Loaded;
        try
        {
            var user = _session.CurrentUser ?? throw new InvalidOperationException("Bạn cần đăng nhập trước.");
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            var order = await ctx.Orders.AsNoTracking().Include(o => o.Tickets)
                .SingleOrDefaultAsync(o => o.Id == _orderId && o.UserId == user.Id)
                ?? throw new InvalidOperationException("Không tìm thấy đơn hàng của bạn.");
            if (order.Status != "Paid")
                throw new InvalidOperationException("Đơn hàng chưa thanh toán.");

            var tickets = order.Tickets.OrderBy(t => t.Id).ToList();
            if (tickets.Count != order.Quantity)
                tickets = await _ticketService.IssueTicketsAsync(_orderId);
            if (tickets.Count == 0 || tickets.Count != order.Quantity)
                throw new InvalidOperationException("Chưa phát hành đủ vé cho đơn hàng.");
            TicketListBox.ItemsSource = tickets;
            StatusTextBlock.Text = $"Đơn #{_orderId}: {tickets.Count} vé";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Không tải được vé: " + ex.Message;
            return;
        }

        await AddToCalendarAsync();
    }

    private async Task AddToCalendarAsync()
    {
        RetryCalendarButton.IsEnabled = false;
        RetryCalendarButton.Visibility = Visibility.Collapsed;
        OpenCalendarButton.IsEnabled = false;
        CalendarStatusTextBlock.Text = "Đang thêm Google Calendar...";
        try
        {
            _eventLink = await _calendar.AddOrderEventAsync(_orderId);
            CalendarStatusTextBlock.Text = "Đã thêm lịch, nhắc trước 1 ngày.";
            OpenCalendarButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            CalendarStatusTextBlock.Text = ex switch
            {
                GoogleApiException google when google.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    => "Không có quyền Calendar. Kiểm tra API/quyền truy cập rồi đăng nhập lại.",
                OperationCanceledException => "Thêm lịch quá thời gian chờ. Bấm Thử lại.",
                _ => "Chưa thêm được lịch: " + ex.Message
            };
            RetryCalendarButton.Visibility = Visibility.Visible;
            RetryCalendarButton.IsEnabled = true;
        }
    }

    private async void RetryCalendarButton_Click(object sender, RoutedEventArgs e)
    {
        await AddToCalendarAsync();
    }

    private void OpenCalendarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_eventLink == null) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_eventLink) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CalendarStatusTextBlock.Text = "Không mở được trình duyệt: " + ex.Message;
        }
    }
}

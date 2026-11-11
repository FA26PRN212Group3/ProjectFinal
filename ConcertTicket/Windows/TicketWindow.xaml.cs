using System.Windows;
using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ConcertTicket.Windows;

//Người làm: Bảo (F5 + F6 - phát hành vé, Google Calendar)
public partial class TicketWindow : Window
{
    private readonly ITicketService _ticketService;
    private readonly ICalendarService _calendar;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly int _orderId;
    private string? _eventLink;

    public TicketWindow(ITicketService ticketService, ICalendarService calendar,
                        IDbContextFactory<ConcertDbContext> dbFactory, int orderId)
    {
        InitializeComponent();
        _ticketService = ticketService;
        _calendar = calendar;
        _dbFactory = dbFactory;
        _orderId = orderId;

        //TODO (Bảo): Loaded += async ... :
        //1. List<Ticket> tickets = await _ticketService.IssueTicketsAsync(_orderId); hiện lên TicketListBox
        //2. _eventLink = await _calendar.AddOrderEventAsync(_orderId); OpenCalendarButton.IsEnabled = true;
        //   (bọc try/catch: rớt mạng thì báo, mở lại cửa sổ là làm tiếp được)
    }

    private void OpenCalendarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_eventLink == null) return;

        //mở link sự kiện bằng trình duyệt mặc định
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_eventLink) { UseShellExecute = true });
    }
}

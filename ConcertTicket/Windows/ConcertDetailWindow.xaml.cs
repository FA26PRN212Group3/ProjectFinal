using System.Windows;
using System.Windows.Controls;
using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ConcertTicket.Windows;

//Người làm: Dương (F3 - chọn vé theo khu)
public partial class ConcertDetailWindow : Window
{
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly IUserSession _session;
    private readonly INavigationService _nav;
    private readonly int _concertId;

    //DI đưa 3 cái đầu, NavigationService đưa thêm concertId
    public ConcertDetailWindow(IDbContextFactory<ConcertDbContext> dbFactory, IUserSession session,
                               INavigationService nav, int concertId)
    {
        InitializeComponent();
        _dbFactory = dbFactory;
        _session = session;
        _nav = nav;
        _concertId = concertId;

        LoadConcert();
    }

    private void LoadConcert()
    {
        using var ctx = _dbFactory.CreateDbContext();

        Concert? concert = ctx.Concerts
            .Include(c => c.Zones)
            .FirstOrDefault(c => c.Id == _concertId);

        if (concert == null)
        {
            MessageBox.Show("Không tìm thấy concert.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        ConcertNameTextBlock.Text = concert.Name;
        ConcertInfoTextBlock.Text = $"{concert.Artist} · {concert.Venue}, {concert.Address} · {concert.StartTime:dd/MM/yyyy HH:mm}";
        ZoneDataGrid.ItemsSource = concert.Zones.OrderByDescending(z => z.Price).ToList();
    }

    private void ZoneDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        //TODO (Dương): tính tổng tiền = Price * số lượng, hiện lên TotalTextBlock
    }

    private void PayButton_Click(object sender, RoutedEventArgs e)
    {
        //TODO (Dương):
        //1. CheckVar(): đã chọn khu? số lượng 1..4? khu còn đủ vé?
        //2. tạo Order Pending (UserId = _session.CurrentUser.Id, ZoneId, Quantity, UnitPrice, TotalAmount)
        //3. if (_nav.ShowPayment(order.Id)) { _nav.ShowTicket(order.Id); Close(); }
        MessageBox.Show("TODO (Dương): tạo đơn và thanh toán.", "Chưa làm", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

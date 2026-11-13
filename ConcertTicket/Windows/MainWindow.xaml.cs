using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Diagnostics;
using System.Globalization;
using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ConcertTicket.Windows;

//Người làm: Bảo (F2 - tìm kiếm)
public partial class MainWindow : Window
{
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly IUserSession _session;
    private readonly IAuthService _auth;
    private readonly INavigationService _nav;
    private bool _loadingPaymentHistory;
    private bool _loadingConcerts;

    public MainWindow(IDbContextFactory<ConcertDbContext> dbFactory, IUserSession session,
                      IAuthService auth, INavigationService nav)
    {
        InitializeComponent();
        _dbFactory = dbFactory;
        _session = session;
        _auth = auth;
        _nav = nav;

        WelcomeTextBlock.Text = "Xin chào, " + _session.CurrentUser?.FullName;
        Loaded += async (_, _) => await LoadConcertsAsync();
    }

    private async Task LoadConcertsAsync()
    {
        if (_loadingConcerts) return;
        _loadingConcerts = true;
        SearchButton.IsEnabled = DetailButton.IsEnabled = ConcertDataGrid.IsEnabled = false;
        ConcertStatusTextBlock.Text = "Đang tìm concert...";
        try
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            if (VenueComboBox.ItemsSource == null)
            {
                var venues = await ctx.Concerts.AsNoTracking().Select(c => c.Venue).Distinct().OrderBy(v => v).ToListAsync();
                VenueComboBox.ItemsSource = new[] { "Tất cả địa điểm" }.Concat(venues).ToList();
                VenueComboBox.SelectedIndex = 0;
            }
            var query = ctx.Concerts.AsNoTracking().AsQueryable();
            string keyword = SearchTextBox.Text.Trim();
            if (keyword.Length > 0) query = query.Where(c => c.Name.Contains(keyword));
            if (VenueComboBox.SelectedIndex > 0 && VenueComboBox.SelectedItem is string venue)
                query = query.Where(c => c.Venue == venue);
            if (ConcertDatePicker.SelectedDate is DateTime date)
                query = query.Where(c => c.StartTime.Date == date.Date);
            var concerts = await query.OrderBy(c => c.StartTime).ToListAsync();
            ConcertDataGrid.ItemsSource = concerts;
            ConcertStatusTextBlock.Text = concerts.Count == 0 ? "Không tìm thấy concert phù hợp." : $"{concerts.Count} concert";
        }
        catch (Exception ex)
        {
            Trace.TraceError("Concert search failed: {0}", ex.GetType().Name);
            ConcertDataGrid.ItemsSource = null;
            ConcertStatusTextBlock.Text = "Không tải được concert. Kiểm tra database rồi bấm Tìm lại.";
        }
        finally
        {
            _loadingConcerts = false;
            SearchButton.IsEnabled = DetailButton.IsEnabled = ConcertDataGrid.IsEnabled = true;
        }
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await LoadConcertsAsync();

    private async void DetailButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenSelectedConcertAsync();
    }

    private async void ConcertDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await OpenSelectedConcertAsync();
    }

    private async Task OpenSelectedConcertAsync()
    {
        if (_loadingConcerts) return;
        Concert? concert = ConcertDataGrid.SelectedItem as Concert;
        if (concert == null)
        {
            MessageBox.Show("Hãy chọn 1 concert trước.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _nav.ShowConcertDetail(concert.Id);   //mở Window của Dương
        await LoadConcertsAsync();            //quay lại thì tải lại, giữ bộ lọc
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        await _auth.LogoutAsync();
        _nav.ShowLogin();
        Close();
    }

    //Huy: mở cửa sổ soát vé (check-in)
    private void CheckInButton_Click(object sender, RoutedEventArgs e)
    {
        _nav.ShowCheckIn();
    }

    private async void PaymentHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingPaymentHistory || _session.CurrentUser is not { Id: > 0 } user) return;
        _loadingPaymentHistory = true;
        PaymentHistoryButton.IsEnabled = false;
        try
        {
            var orders = await ReadPaymentOrdersAsync(user.Id);
            if (_session.CurrentUser?.Id != user.Id || !IsLoaded) return;
            var menu = new ContextMenu { PlacementTarget = PaymentHistoryButton };
            foreach (Order order in orders)
            {
                string status = order.Status switch { "Paid" => "Đã xác nhận", "Failed" => "Không thành công", _ => "Chờ kiểm tra" };
                var item = new MenuItem { Header = $"Đơn #{order.Id} · {order.TotalAmount.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"))} VND · {status}" };
                item.Click += (_, _) => OpenPaymentHistoryOrder(order.Id);
                menu.Items.Add(item);
            }
            if (orders.Count == 0) menu.Items.Add(new MenuItem { Header = "Chưa có phiên thanh toán được ghi nhận.", IsEnabled = false });
            PaymentHistoryButton.ContextMenu = menu;
            menu.IsOpen = true;
        }
        catch (Exception exception)
        {
            Trace.TraceError("Payment history failed: {0}", exception.GetType().Name);
            MessageBox.Show("Không tải được lịch sử thanh toán. Kiểm tra database và migration PaymentRecovery.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _loadingPaymentHistory = false; PaymentHistoryButton.IsEnabled = true; }
    }

    private void OpenPaymentHistoryOrder(int orderId)
    {
        try { if (_nav.ShowPayment(orderId)) _nav.ShowTicket(orderId); }
        catch (Exception exception)
        {
            Trace.TraceError("Payment history navigation failed: {0}", exception.GetType().Name);
            MessageBox.Show("Không mở được đơn thanh toán. Vui lòng thử lại.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task<List<Order>> ReadPaymentOrdersAsync(int userId)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync();
        return await ctx.Orders.AsNoTracking().Where(o => o.UserId == userId
                && (ctx.PaymentCheckouts.Any(c => c.OrderId == o.Id) || ctx.Payments.Any(p => p.OrderId == o.Id)))
            .OrderByDescending(o => o.CreatedAt).Take(20).ToListAsync();
    }
}

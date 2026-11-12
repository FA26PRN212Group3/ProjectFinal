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

    public MainWindow(IDbContextFactory<ConcertDbContext> dbFactory, IUserSession session,
                      IAuthService auth, INavigationService nav)
    {
        InitializeComponent();
        _dbFactory = dbFactory;
        _session = session;
        _auth = auth;
        _nav = nav;

        WelcomeTextBlock.Text = "Xin chào, " + _session.CurrentUser?.FullName;
        LoadConcerts();
    }

    private void LoadConcerts()
    {
        //mỗi lần cần thì tạo context mới, dùng xong tự đóng => dữ liệu luôn mới
        using var ctx = _dbFactory.CreateDbContext();

        //TODO (Bảo): ghép thêm điều kiện tìm kiếm bằng AsQueryable() + Where
        ConcertDataGrid.ItemsSource = ctx.Concerts.OrderBy(c => c.StartTime).ToList();
    }

    private void DetailButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedConcert();
    }

    private void ConcertDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedConcert();
    }

    private void OpenSelectedConcert()
    {
        Concert? concert = ConcertDataGrid.SelectedItem as Concert;
        if (concert == null)
        {
            MessageBox.Show("Hãy chọn 1 concert trước.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _nav.ShowConcertDetail(concert.Id);   //mở Window của Dương
        LoadConcerts();                       //quay lại thì tải lại
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        await _auth.LogoutAsync();
        _nav.ShowLogin();
        Close();
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

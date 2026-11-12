using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    private bool _isLoading = true;
    private bool _isSubmitting;
    private bool _hasConcert;
    private bool _isClosed;
    private Order? _pendingOrder;

    //DI đưa 3 cái đầu, NavigationService đưa thêm concertId
    public ConcertDetailWindow(IDbContextFactory<ConcertDbContext> dbFactory, IUserSession session,
                               INavigationService nav, int concertId)
    {
        InitializeComponent();
        _dbFactory = dbFactory;
        _session = session;
        _nav = nav;
        _concertId = concertId;

        _isLoading = false;
        Loaded += async (_, _) => await LoadConcertAsync();
        Closed += (_, _) => _isClosed = true;
        UpdateControlState();
    }

    private async Task LoadConcertAsync()
    {
        if (_isClosed || _isLoading || _isSubmitting) return;
        int? selectedZoneId = (ZoneDataGrid.SelectedItem as Zone)?.Id;
        _isLoading = true;
        _hasConcert = false;
        UpdateControlState();
        BookingStatusTextBlock.Text = "Đang tải thông tin concert...";
        try
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            Concert? concert = await ctx.Concerts.AsNoTracking()
                .Include(c => c.Zones)
                .FirstOrDefaultAsync(c => c.Id == _concertId);
            if (_isClosed) return;
            if (concert == null)
            {
                ZoneDataGrid.ItemsSource = null;
                BookingStatusTextBlock.Text = "Không tìm thấy concert.";
                return;
            }

            ConcertNameTextBlock.Text = concert.Name;
            ConcertInfoTextBlock.Text = $"{concert.Artist} · {concert.Venue}, {concert.Address} · {concert.StartTime:dd/MM/yyyy HH:mm}";
            var zones = concert.Zones.OrderByDescending(z => z.Price).ToList();
            ZoneDataGrid.ItemsSource = zones;
            ZoneDataGrid.SelectedItem = zones.FirstOrDefault(z => z.Id == selectedZoneId);
            _hasConcert = true;
            BookingStatusTextBlock.Text = "";
        }
        catch (Exception ex)
        {
            Trace.TraceError("F3 load failed: {0}", ex.GetType().Name);
            if (!_isClosed)
            {
                ZoneDataGrid.ItemsSource = null;
                BookingStatusTextBlock.Text = "Không thể tải dữ liệu. Vui lòng kiểm tra kết nối database và bấm Tải lại.";
            }
        }
        finally
        {
            _isLoading = false;
            if (!_isClosed) UpdateTotal();
        }
    }

    private void ZoneDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading) UpdateTotal(showValidation: true);
    }

    private void QuantityTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoading) UpdateTotal(showValidation: true);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSubmitting && !_isLoading) await LoadConcertAsync();
    }

    private bool TryGetBookingInput(out Zone? zone, out int quantity, out string error)
    {
        zone = ZoneDataGrid.SelectedItem as Zone;
        quantity = 0;
        error = "";
        if (!_hasConcert || zone == null || zone.ConcertId != _concertId)
            error = "Hãy chọn một khu vé của concert.";
        else if (!int.TryParse(QuantityTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out quantity)
                 || quantity < 1 || quantity > 4)
            error = "Số lượng vé phải là số nguyên từ 1 đến 4.";
        else if (quantity > zone.Remaining)
            error = "Khu đã chọn không còn đủ vé.";
        else if (!IsValidTotal(zone.Price, quantity))
            error = "Giá vé không hợp lệ.";
        else if (_session.CurrentUser == null || _session.CurrentUser.Id <= 0)
            error = "Vui lòng đăng nhập trước khi đặt vé.";
        return error.Length == 0;
    }

    private static bool IsValidTotal(decimal price, int quantity) =>
        price > 0 && decimal.Truncate(price) == price
        && price <= 999_999_999_999_999_999m / quantity;

    private void UpdateTotal(bool showValidation = false)
    {
        bool valid = TryGetBookingInput(out Zone? zone, out int quantity, out string error);
        TotalTextBlock.Text = valid
            ? "Tổng tiền: " + (zone!.Price * quantity).ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) + " VND"
            : "Tổng tiền: --";
        if (showValidation) BookingStatusTextBlock.Text = error;
        UpdateControlState();
    }

    private void UpdateControlState()
    {
        bool editable = !_isLoading && !_isSubmitting && !_isClosed && _hasConcert;
        ZoneDataGrid.IsEnabled = editable;
        QuantityTextBox.IsEnabled = editable;
        RefreshButton.IsEnabled = !_isLoading && !_isSubmitting && !_isClosed;
        PayButton.IsEnabled = editable && TryGetBookingInput(out _, out _, out _);
    }

    private async Task<Order?> CreateOrResumeOrderAsync(int zoneId, int quantity, int userId)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync();
        if (!await ctx.Users.AnyAsync(u => u.Id == userId))
        {
            BookingStatusTextBlock.Text = "Không tìm thấy người dùng. Vui lòng đăng nhập lại.";
            return null;
        }

        Zone? zone = await ctx.Zones.AsNoTracking()
            .FirstOrDefaultAsync(z => z.Id == zoneId && z.ConcertId == _concertId);
        if (zone != null && ZoneDataGrid.SelectedItem is Zone displayed && displayed.Id == zone.Id)
        {
            displayed.Price = zone.Price;
            displayed.TotalQuantity = zone.TotalQuantity;
            displayed.SoldQuantity = zone.SoldQuantity;
            ZoneDataGrid.Items.Refresh();
            UpdateTotal();
        }
        if (zone == null || quantity > zone.Remaining || !IsValidTotal(zone.Price, quantity))
        {
            BookingStatusTextBlock.Text = "Khu vé không còn khả dụng hoặc không đủ vé. Vui lòng bấm Tải lại.";
            return null;
        }

        // Mở lại cửa sổ thanh toán dùng đơn Pending cũ thay vì lưu thêm đơn.
        if (_pendingOrder != null && _pendingOrder.UserId == userId
            && _pendingOrder.ZoneId == zoneId && _pendingOrder.Quantity == quantity)
        {
            Order? saved = await ctx.Orders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == _pendingOrder.Id && o.UserId == userId);
            if (saved?.Status == "Pending") return saved;
            if (saved?.Status == "Paid")
            {
                BookingStatusTextBlock.Text = "Đơn này đã thanh toán. Vui lòng kiểm tra vé trước khi đặt lại.";
                return null;
            }
        }

        var order = new Order
        {
            UserId = userId,
            ZoneId = zone.Id,
            Quantity = quantity,
            UnitPrice = zone.Price,
            TotalAmount = zone.Price * quantity,
            Status = "Pending"
        };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();
        return order;
    }

    private async void PayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _isSubmitting || _isClosed) return;
        if (!TryGetBookingInput(out Zone? zone, out int quantity, out string error))
        {
            BookingStatusTextBlock.Text = error;
            return;
        }

        _isSubmitting = true;
        UpdateControlState();
        try
        {
            Order? order;
            try
            {
                order = await CreateOrResumeOrderAsync(zone!.Id, quantity, _session.CurrentUser!.Id);
            }
            catch (Exception ex)
            {
                Trace.TraceError("F3 save failed: {0}", ex.GetType().Name);
                BookingStatusTextBlock.Text = "Không thể tạo đơn hàng. Vui lòng kiểm tra kết nối database và thử lại.";
                return;
            }
            if (order == null) return;
            _pendingOrder = order;
            TotalTextBlock.Text = "Tổng tiền: " + order.TotalAmount.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) + " VND";
            BookingStatusTextBlock.Text = $"Đơn #{order.Id} đã được lưu, đang chờ thanh toán.";

            try
            {
                if (_nav.ShowPayment(order.Id))
                {
                    _nav.ShowTicket(order.Id);
                    _isSubmitting = false;
                    Close();
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("F3 navigation failed: {0}", ex.GetType().Name);
                BookingStatusTextBlock.Text = $"Đơn #{order.Id} đã được lưu nhưng không mở được màn hình tiếp theo. Bạn có thể thử lại.";
            }
        }
        finally
        {
            _isSubmitting = false;
            if (!_isClosed) UpdateControlState();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isSubmitting)
        {
            e.Cancel = true;
            BookingStatusTextBlock.Text = "Vui lòng chờ thao tác đặt vé hoàn tất.";
        }
        base.OnClosing(e);
    }
}

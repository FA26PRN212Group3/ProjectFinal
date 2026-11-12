using System.Diagnostics;
using System.Globalization;
using System.Windows;
using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Web.WebView2.Core;

namespace ConcertTicket.Windows;

//Người làm: Dương (F4 - thanh toán VNPay)
public partial class PaymentWindow : Window
{
    private readonly IPaymentService _payment;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly int _orderId;
    private readonly IUserSession _session;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _loading;
    private bool _checking;
    private bool _returnProcessing;
    private bool _returnHandled;
    private bool _browserLoading;
    private bool _closed;
    private bool _canLoad = true;
    private PaymentState _state = PaymentState.Pending;

    public PaymentWindow(IPaymentService payment, IDbContextFactory<ConcertDbContext> dbFactory,
                         IUserSession session, int orderId)
    {
        InitializeComponent();
        _payment = payment;
        _dbFactory = dbFactory;
        _orderId = orderId;
        _session = session;

        Loaded += PaymentWindow_Loaded;
        Closed += PaymentWindow_Closed;
        PaymentWebView.NavigationStarting += PaymentWebView_NavigationStarting;
        PaymentWebView.NavigationCompleted += PaymentWebView_NavigationCompleted;
    }

    private bool IsBusy => _loading || _checking || _returnProcessing;

    private async void PaymentWindow_Loaded(object sender, RoutedEventArgs e) => await LoadPaymentAsync();
    private async void ReloadButton_Click(object sender, RoutedEventArgs e) => await LoadPaymentAsync();
    private async void CheckStatusButton_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task LoadPaymentAsync()
    {
        if (_closed || IsBusy || !_canLoad || _returnHandled) return;
        _loading = true;
        UpdateControls();
        PaymentStatusTextBlock.Text = "Đang chuẩn bị thanh toán...";
        try
        {
            Order order = await ReadOwnedOrderAsync();
            if (_closed) return;
            OrderSummaryTextBlock.Text = $"Đơn #{order.Id} · {order.TotalAmount.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"))} VND";
            PaymentResult status = await _payment.CheckPaymentStatusAsync(_orderId, _lifetime.Token);
            if (_closed) return;
            if (status.State != PaymentState.Pending) { Display(status); return; }

            string url = await _payment.CreatePaymentUrlAsync(order, _lifetime.Token);
            if (_closed) return;
            bool initialize = PaymentWebView.CoreWebView2 == null;
            await PaymentWebView.EnsureCoreWebView2Async();
            if (_closed) return;
            CoreWebView2 core = PaymentWebView.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 chưa khởi tạo thành công.");
            if (initialize)
            {
                core.NewWindowRequested += Core_NewWindowRequested;
                core.ProcessFailed += Core_ProcessFailed;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
            }
            PaymentWebView.Visibility = Visibility.Visible;
            PaymentStatusTextBlock.Text = "Đang tải cổng thanh toán VNPAY Sandbox...";
            core.Navigate(url);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            ReportError(ex, "Không thể mở thanh toán. Kiểm tra kết nối database, cấu hình VNPAY, mạng và WebView2 Runtime; sau đó tải lại hoặc kiểm tra xác nhận.");
        }
        finally
        {
            _loading = false;
            if (!_closed) UpdateControls();
        }
    }

    private async Task<Order> ReadOwnedOrderAsync()
    {
        int? userId = _session.CurrentUser?.Id;
        if (userId is null or <= 0) throw new InvalidOperationException("Không có phiên đăng nhập hợp lệ.");
        await using var ctx = await _dbFactory.CreateDbContextAsync(_lifetime.Token);
        return await ctx.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == _orderId && o.UserId == userId, _lifetime.Token)
            ?? throw new InvalidOperationException("Không tìm thấy đơn thuộc người dùng hiện tại.");
    }

    private async void PaymentWebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_closed) { e.Cancel = true; return; }
        try
        {
            if (_payment.IsReturnUrl(e.Uri))
            {
                // Chặn trước khi trình duyệt truy cập localhost; không cần HTTP listener cho ReturnUrl.
                e.Cancel = true;
                await HandleReturnAsync(e.Uri);
            }
            else if (_returnHandled || !IsHttps(e.Uri))
            {
                e.Cancel = true;
                if (!_returnHandled) ReportError(null, "Cổng thanh toán chuyển tới liên kết không được hỗ trợ.");
            }
            else
            {
                _browserLoading = true;
                UpdateControls();
                PaymentStatusTextBlock.Text = "Đang tải trang thanh toán...";
            }
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            ReportError(ex, "Không thể xử lý chuyển hướng thanh toán. Vui lòng kiểm tra xác nhận.");
        }
    }

    private async Task HandleReturnAsync(string url)
    {
        if (_closed || _returnHandled) return;
        _returnHandled = true;
        _browserLoading = false;
        _returnProcessing = true;
        _canLoad = false;
        PaymentWebView.Visibility = Visibility.Collapsed;
        PaymentStatusTextBlock.Text = "Đang xác thực phản hồi thanh toán...";
        UpdateControls();
        try
        {
            await ReadOwnedOrderAsync();
            PaymentResult result = await _payment.ProcessReturnUrlAsync(url, _lifetime.Token);
            if (_closed) return;
            if (result.OrderId != _orderId)
                result = new PaymentResult { Message = "Phản hồi không hợp lệ hoặc không thuộc đơn đang mở." };
            Display(result);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            ReportError(ex, "Chưa thể xác thực phản hồi. Đơn vẫn chờ xác nhận; vui lòng bấm Kiểm tra xác nhận.");
        }
        finally
        {
            _returnProcessing = false;
            if (!_closed) UpdateControls();
        }
    }

    private void PaymentWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_closed || _returnHandled) return;
        _browserLoading = false;
        UpdateControls();
        if (!e.IsSuccess || e.HttpStatusCode >= 400)
        {
            if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                ReportError(null, "Không tải được trang thanh toán. Bạn có thể tải lại cùng phiên hoặc kiểm tra xác nhận.");
            return;
        }
        PaymentStatusTextBlock.Text = "Vui lòng thực hiện thanh toán trên VNPAY Sandbox. Đóng cửa sổ không làm đơn thất bại.";
    }

    private async void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (_closed) return;
        try
        {
            if (_payment.IsReturnUrl(e.Uri)) await HandleReturnAsync(e.Uri);
            else if (!_returnHandled && IsHttps(e.Uri)) PaymentWebView.CoreWebView2.Navigate(e.Uri);
        }
        catch (Exception ex) { ReportError(ex, "Không thể mở trang thanh toán bổ sung. Vui lòng kiểm tra xác nhận."); }
    }

    private void Core_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e) =>
        ReportError(null, "WebView2 đã dừng. Kiểm tra xác nhận trước khi mở lại ứng dụng.");

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;

    private async Task RefreshStatusAsync(bool continueToTickets = false)
    {
        if (_closed || IsBusy) return;
        _checking = true;
        UpdateControls();
        PaymentStatusTextBlock.Text = "Đang kiểm tra xác nhận trong hệ thống...";
        try
        {
            await ReadOwnedOrderAsync();
            PaymentResult result = await _payment.CheckPaymentStatusAsync(_orderId, _lifetime.Token);
            if (_closed) return;
            Display(result);
            // Chỉ thao tác này mới cho F3 đi tiếp tới TicketWindow sau xác nhận DB.
            if (continueToTickets && result.OrderId == _orderId && result.IsSuccess && result.State == PaymentState.Confirmed)
                DialogResult = true;
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { ReportError(ex, "Không thể kiểm tra xác nhận. Đơn không bị thay đổi; vui lòng thử lại."); }
        finally
        {
            _checking = false;
            if (!_closed) UpdateControls();
        }
    }

    private async void ContinueButton_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync(continueToTickets: true);

    private void Display(PaymentResult result)
    {
        _state = result.State;
        _canLoad = result.State == PaymentState.Pending && !_returnHandled;
        PaymentStatusTextBlock.Text = result.Message;
        if (result.State != PaymentState.Pending)
        {
            _browserLoading = false;
            PaymentWebView.Visibility = Visibility.Collapsed;
        }
        ContinueButton.Visibility = result.IsSuccess && result.State == PaymentState.Confirmed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReportError(Exception? exception, string message)
    {
        if (_closed) return;
        // Không log Exception.Message/URL vì có thể chứa dữ liệu thanh toán hoặc cấu hình.
        if (exception != null) Trace.TraceError("F4 payment failed: {0}", exception.GetType().Name);
        _state = PaymentState.Invalid;
        _browserLoading = false;
        PaymentStatusTextBlock.Text = message;
        PaymentWebView.Visibility = Visibility.Collapsed;
        ContinueButton.Visibility = Visibility.Collapsed;
        LoadingProgressBar.Visibility = Visibility.Collapsed;
        UpdateControls();
    }

    private void UpdateControls()
    {
        LoadingProgressBar.Visibility = IsBusy || _browserLoading ? Visibility.Visible : Visibility.Collapsed;
        ReloadButton.IsEnabled = !IsBusy && !_browserLoading && _canLoad && !_returnHandled;
        CheckStatusButton.IsEnabled = !IsBusy;
        ContinueButton.IsEnabled = !IsBusy && _state == PaymentState.Confirmed;
    }

    private void PaymentWindow_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        Loaded -= PaymentWindow_Loaded;
        PaymentWebView.NavigationStarting -= PaymentWebView_NavigationStarting;
        PaymentWebView.NavigationCompleted -= PaymentWebView_NavigationCompleted;
        if (PaymentWebView.CoreWebView2 != null)
        {
            PaymentWebView.CoreWebView2.NewWindowRequested -= Core_NewWindowRequested;
            PaymentWebView.CoreWebView2.ProcessFailed -= Core_ProcessFailed;
        }
        PaymentWebView.Dispose();
        _lifetime.Dispose();
    }
}

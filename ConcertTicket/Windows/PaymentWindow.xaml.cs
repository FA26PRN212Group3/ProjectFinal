using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Web.WebView2.Core;

namespace ConcertTicket.Windows;

//Người làm: Dương (F4 - thanh toán VNPay)
public partial class PaymentWindow : Window
{
    private const string CustomerPaymentError = "Chưa xác nhận được thanh toán, vui lòng thử lại sau hoặc liên hệ hỗ trợ.";
    private readonly IPaymentService _payment;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly int _orderId;
    private readonly IUserSession _session;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _restoringReturnState;
    private bool _automaticReconciliation;
    private DateTimeOffset _nextAutomaticReconciliation;
    private bool _loading;
    private bool _checking;
    private bool _returnProcessing;
    private bool _returnHandled;
    private bool _browserLoading;
    private bool _closed;
    private bool _orderPending;
    private bool _navigationCompleted;
    private bool _canLoad = true;
    private PaymentState _state = PaymentState.Pending;
    private PaymentResult? _lastReconciliation;

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
        _statusTimer.Tick += StatusTimer_Tick;
    }

    private bool IsBusy => _loading || _checking || _returnProcessing;

    private async void PaymentWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _statusTimer.Start();
        await LoadPaymentAsync();
    }
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
            await RestoreReturnStateAsync();
            PaymentResult status = await _payment.CheckPaymentStatusAsync(_orderId, _lifetime.Token);
            if (_closed) return;
            if (status.State != PaymentState.Pending || _returnHandled)
            {
                Display(status);
                CompletePayment(status);
                if (_closed) return;
                if (_returnHandled || status.State == PaymentState.Expired)
                {
                    _automaticReconciliation = true;
                    await ConfirmWithProviderAsync();
                    if (!_closed && _state == PaymentState.Confirmed)
                    {
                        status = await _payment.CheckPaymentStatusAsync(_orderId, _lifetime.Token);
                        if (_closed) return;
                        Display(status);
                        CompletePayment(status);
                    }
                }
                return;
            }

            string url = await _payment.CreatePaymentUrlAsync(order, _lifetime.Token);
            await RestoreReturnStateAsync();
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
            SizeToContent = SizeToContent.Manual;
            Height = 640;
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
        Order order = await ctx.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == _orderId && o.UserId == userId, _lifetime.Token)
            ?? throw new InvalidOperationException("Không tìm thấy đơn thuộc người dùng hiện tại.");
        _orderPending = order.Status == "Pending";
        return order;
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
        HidePaymentPage();
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
            if (result.OrderId == _orderId && result.IsVerified && result.ResponseCode == "00"
                && result.State == PaymentState.AwaitingConfirmation)
            {
                _automaticReconciliation = true;
                await ConfirmWithProviderAsync();
            }
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
        if (!_closed && _state == PaymentState.Confirmed)
            await RefreshStatusAsync();
    }

    private async void PaymentWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
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
        PaymentStatusTextBlock.Text = "Vui lòng hoàn tất thanh toán trên VNPAY Sandbox. Nếu đã bị trừ tiền, vui lòng không thanh toán lại; hãy kiểm tra trạng thái sau hoặc liên hệ hỗ trợ.";
        try
        {
            await _payment.RecordPaymentPageOpenedAsync(_orderId, _lifetime.Token);
            await RestoreReturnStateAsync();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception) { Trace.TraceError("Payment activity failed: {0}", exception.GetType().Name); }
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

    private async Task RefreshStatusAsync(bool allowReconciliation = false)
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
            if (result.State is PaymentState.Pending or PaymentState.AwaitingConfirmation or PaymentState.Expired
                && _lastReconciliation?.State == PaymentState.AwaitingConfirmation)
                Display(_lastReconciliation);
            else
                Display(result);
            if (allowReconciliation && _automaticReconciliation && DateTimeOffset.UtcNow >= _nextAutomaticReconciliation)
            {
                await ConfirmWithProviderAsync();
                if (_closed) return;
                result = await _payment.CheckPaymentStatusAsync(_orderId, _lifetime.Token);
                if (_closed) return;
                Display(result);
            }
            if (_closed) return;
            CompletePayment(result);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { ReportError(ex, "Không thể kiểm tra xác nhận. Đơn không bị thay đổi; vui lòng thử lại."); }
        finally
        {
            _checking = false;
            if (!_closed) UpdateControls();
        }
        await RestoreReturnStateAsync();
    }

    private async Task ConfirmWithProviderAsync()
    {
        if (_closed || _state is PaymentState.Confirmed or PaymentState.ReconciliationRequired)
            return;
        await ReadOwnedOrderAsync();
        _nextAutomaticReconciliation = DateTimeOffset.UtcNow.AddMinutes(5);
        PaymentStatusTextBlock.Text = "Đang xác minh thanh toán trực tiếp với VNPAY...";
        PaymentResult result = await _payment.ReconcilePaymentAsync(_orderId, _lifetime.Token);
        if (_closed) return;
        if (result.OrderId != _orderId)
            result = new PaymentResult { Message = "Kết quả đối soát không thuộc đơn đang mở." };
        _lastReconciliation = result;
        Display(result);
        await RestoreReturnStateAsync();
    }

    private async void StatusTimer_Tick(object? sender, EventArgs e)
    {
        if (!_closed && !IsBusy && !_browserLoading) await RefreshStatusAsync(allowReconciliation: true);
    }

    private async Task RestoreReturnStateAsync()
    {
        if (_closed || _restoringReturnState || _returnHandled) return;
        _restoringReturnState = true;
        try
        {
            await ReadOwnedOrderAsync();
            IReadOnlyList<PaymentActivity> activities = await _payment.GetPaymentActivityAsync(_orderId, _lifetime.Token);
            if (_closed) return;

            if (!_returnHandled && activities.Any(a => a.EventType == "ReturnUrlReceived"))
            {
                _returnHandled = true;
                _automaticReconciliation = true;
                _canLoad = false;
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception)
        {
            Trace.TraceError("Payment return-state recovery failed: {0}", exception.GetType().Name);
        }
        finally { _restoringReturnState = false; }
    }

    private void CompletePayment(PaymentResult databaseResult)
    {
        if (_closed || _navigationCompleted || databaseResult.OrderId != _orderId
            || !databaseResult.IsSuccess || databaseResult.State != PaymentState.Confirmed) return;
        _navigationCompleted = true;
        _statusTimer.Stop();
        DialogResult = true;
    }

    private void Display(PaymentResult result)
    {
        _state = result.State;
        if (result.State is PaymentState.Confirmed or PaymentState.Declined or PaymentState.Cancelled or PaymentState.ReconciliationRequired)
            _automaticReconciliation = false;
        _canLoad = result.State == PaymentState.Pending && !_returnHandled;
        Trace.TraceInformation("Payment status for order {0}: {1}; confirmed={2}", _orderId, result.State, result.IsSuccess);
        PaymentStatusTextBlock.Text = result.State switch
        {
            PaymentState.Confirmed when result.IsSuccess => "Thanh toán thành công. Vé của bạn đang được chuẩn bị.",
            PaymentState.Pending => "Đơn đang chờ thanh toán. Nếu đã bị trừ tiền, vui lòng không thanh toán lại; hãy kiểm tra trạng thái sau hoặc liên hệ hỗ trợ.",
            PaymentState.AwaitingConfirmation => "Đang chờ xác nhận thanh toán. Nếu đã bị trừ tiền, vui lòng không thanh toán lại; hãy kiểm tra trạng thái sau hoặc liên hệ hỗ trợ.",
            PaymentState.Cancelled => "Bạn đã hủy thanh toán.",
            PaymentState.Declined => "Thanh toán chưa thành công. Vui lòng thử lại sau hoặc liên hệ hỗ trợ.",
            PaymentState.Expired => "Phiên thanh toán đã hết hạn. Vui lòng kiểm tra lại thanh toán hoặc liên hệ hỗ trợ.",
            PaymentState.ReconciliationRequired => "Thanh toán cần được hỗ trợ thêm trước khi nhận vé. Vui lòng liên hệ hỗ trợ và không thanh toán lại.",
            _ => CustomerPaymentError
        };
        if (result.State != PaymentState.Pending)
        {
            _browserLoading = false;
            HidePaymentPage();
        }
        UpdateControls();
    }

    private void ReportError(Exception? exception, string message)
    {
        if (_closed) return;
        // Không log Exception.Message/URL vì có thể chứa dữ liệu thanh toán hoặc cấu hình.
        Trace.TraceError("F4 payment failed: {0}; context={1}", exception?.GetType().Name ?? "PaymentError", message);
        _state = PaymentState.Invalid;
        _browserLoading = false;
        PaymentStatusTextBlock.Text = CustomerPaymentError;
        HidePaymentPage();
        LoadingProgressBar.Visibility = Visibility.Collapsed;
        UpdateControls();
    }

    private void UpdateControls()
    {
        LoadingProgressBar.Visibility = IsBusy || _browserLoading ? Visibility.Visible : Visibility.Collapsed;
        CheckStatusButton.Visibility = _orderPending ? Visibility.Visible : Visibility.Collapsed;
        CheckStatusButton.IsEnabled = !IsBusy;
    }

    private void HidePaymentPage()
    {
        PaymentWebView.Visibility = Visibility.Collapsed;
        SizeToContent = SizeToContent.Height;
    }

    private void PaymentWindow_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _statusTimer.Stop();
        _statusTimer.Tick -= StatusTimer_Tick;
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

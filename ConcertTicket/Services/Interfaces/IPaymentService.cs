using ConcertTicket.Models;

namespace ConcertTicket.Services.Interfaces;

//Người viết: Dương
public interface IPaymentService
{
    //tạo URL VNPay Sandbox có chữ ký HMAC-SHA512 cho đơn Pending
    string CreatePaymentUrl(Order order);

    //WebView2 hỏi: URL sắp chuyển tới có phải ReturnUrl không?
    bool IsReturnUrl(string url);

    // Return chỉ xác thực và đọc DB; IPN mới được xác nhận thanh toán và cập nhật tồn kho.
    PaymentResult ProcessReturnUrl(string url);

    Task<string> CreatePaymentUrlAsync(Order order, CancellationToken cancellationToken = default);

    Task<PaymentResult> ProcessReturnUrlAsync(string url, CancellationToken cancellationToken = default);

    // Đọc xác nhận do backend/IPN lưu, không gọi QueryDR hoặc tự xác nhận.
    Task<PaymentResult> CheckPaymentStatusAsync(int orderId, CancellationToken cancellationToken = default);

    Task<VnPayIpnResponse> ProcessIpnAsync(string queryString, CancellationToken cancellationToken = default);

    Task<PaymentResult> ReconcilePaymentAsync(int orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaymentActivity>> GetPaymentActivityAsync(int orderId, CancellationToken cancellationToken = default);

    Task RecordPaymentPageOpenedAsync(int orderId, CancellationToken cancellationToken = default);
}

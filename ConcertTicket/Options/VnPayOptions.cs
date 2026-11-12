namespace ConcertTicket.Options;

//đọc từ mục "VnPay" trong appsettings.json / user-secrets
public class VnPayOptions
{
    public string TmnCode { get; set; } = "";
    public string HashSecret { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ReturnUrl { get; set; } = "";

    public int PaymentTimeoutMinutes { get; set; } = 15;

    // Sandbox desktop không có HTTP request để lấy IP của trình duyệt.
    public string IpAddress { get; set; } = "127.0.0.1";
}

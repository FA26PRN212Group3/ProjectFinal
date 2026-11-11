using ConcertTicket.Models;
using ConcertTicket.Options;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConcertTicket.Services;

//Người viết: Dương (F4)
public class VnPayService : IPaymentService
{
    //chỉ đọc _options.Value bên trong hàm (lý do: xem GoogleAuthService)
    private readonly IOptions<VnPayOptions> _options;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly ILogger<VnPayService> _logger;

    public VnPayService(IOptions<VnPayOptions> options, IDbContextFactory<ConcertDbContext> dbFactory,
                        ILogger<VnPayService> logger)
    {
        _options = options;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public string CreatePaymentUrl(Order order)
    {
        //TODO (Dương): ghép tham số vnp_* (Amount = TotalAmount * 100, TxnRef = order.Id, ReturnUrl...),
        //sắp xếp theo tên, ký HMAC-SHA512 bằng _options.Value.HashSecret
        throw new NotImplementedException("TODO (Dương): CreatePaymentUrl");
    }

    public bool IsReturnUrl(string url)
    {
        return url.StartsWith(_options.Value.ReturnUrl, StringComparison.OrdinalIgnoreCase);
    }

    public PaymentResult ProcessReturnUrl(string url)
    {
        //TODO (Dương): đọc query string, kiểm vnp_SecureHash,
        //rồi 1 lần SaveChanges: thêm Payment + Order Paid/Failed + PaidAt + cộng Zone.SoldQuantity
        throw new NotImplementedException("TODO (Dương): ProcessReturnUrl");
    }
}

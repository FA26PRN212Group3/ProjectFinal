using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using ConcertTicket.Models;
using ConcertTicket.Options;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QRCoder;

namespace ConcertTicket.Services;

//Người viết: Bảo (F5)
public class TicketService : ITicketService
{
    //chỉ đọc _options.Value bên trong hàm (lý do: xem GoogleAuthService)
    private readonly IOptions<CloudinaryOptions> _options;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly ILogger<TicketService> _logger;
    private readonly IUserSession _session;
    private readonly IHttpClientFactory _httpClients;

    public TicketService(IOptions<CloudinaryOptions> options, IDbContextFactory<ConcertDbContext> dbFactory,
                          ILogger<TicketService> logger, IUserSession session, IHttpClientFactory httpClients)
    {
        _options = options;
        _dbFactory = dbFactory;
        _logger = logger;
        _session = session;
        _httpClients = httpClients;
    }

    public async Task<List<Ticket>> IssueTicketsAsync(int orderId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(orderId);
        var user = _session.CurrentUser ?? throw new InvalidOperationException("Bạn cần đăng nhập trước.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var ctx = await _dbFactory.CreateDbContextAsync(timeout.Token);
        Order order;
        await using (var transaction = await ctx.Database.BeginTransactionAsync(timeout.Token))
        {
            // Khóa đơn trong DB để hai cửa sổ hoặc hai tiến trình không phát hành thừa vé.
            order = await ctx.Orders.FromSqlInterpolated($"SELECT * FROM dbo.[Order] WITH (UPDLOCK, HOLDLOCK) WHERE Id={orderId} AND UserId={user.Id}")
                .Include(o => o.Zone).ThenInclude(z => z.Concert).Include(o => o.Tickets)
                .SingleOrDefaultAsync(timeout.Token)
                ?? throw new InvalidOperationException("Không tìm thấy đơn hàng của bạn.");
            if (order.Status != "Paid")
                throw new InvalidOperationException("Chỉ phát hành vé cho đơn đã thanh toán.");
            if (order.Quantity is < 1 or > 4 || order.Tickets.Count > order.Quantity || string.IsNullOrWhiteSpace(order.Zone.Gate))
                throw new InvalidOperationException("Số lượng vé hoặc cổng vào của đơn không hợp lệ.");

            while (order.Tickets.Count < order.Quantity)
            {
                string code;
                do { code = GenerateTicketCode(order.Zone.Concert.StartTime); }
                while (order.Tickets.Any(t => t.TicketCode == code));
                order.Tickets.Add(new Ticket { TicketCode = code, Gate = order.Zone.Gate, Status = "Valid" });
            }
            await ctx.SaveChangesAsync(timeout.Token);
            await transaction.CommitAsync(timeout.Token);
        }

        var missingQr = order.Tickets.Where(t => string.IsNullOrWhiteSpace(t.QrImageUrl)).ToList();
        if (missingQr.Count > 0)
        {
            var options = _options.Value;
            if (string.IsNullOrWhiteSpace(options.CloudName) || string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.ApiSecret))
                throw new InvalidOperationException("Thiếu cấu hình Cloudinary (CloudName, ApiKey, ApiSecret). Mã vé đã lưu; hãy bổ sung cấu hình rồi thử lại vé.");
            using var client = _httpClients.CreateClient("TicketQr");
            var cloudinary = new Cloudinary(new Account(options.CloudName, options.ApiKey, options.ApiSecret));
            cloudinary.Api.Client = client;
            foreach (var ticket in missingQr)
            {
                using var data = QRCodeGenerator.GenerateQrCode(ticket.TicketCode, QRCodeGenerator.ECCLevel.Q);
                using var qr = new PngByteQRCode(data);
                using var png = new MemoryStream(qr.GetGraphic(8));
                ImageUploadResult uploaded;
                try
                {
                    // PublicId cố định giữ một ảnh cho mỗi mã vé khi upload hoặc lưu DB phải thử lại.
                    uploaded = await cloudinary.UploadAsync(new ImageUploadParams
                    {
                        File = new FileDescription(ticket.TicketCode + ".png", png),
                        Folder = options.Folder, PublicId = ticket.TicketCode, Overwrite = false
                    }, timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Ticket QR upload failed with {ErrorType} for order {OrderId}.", ex.GetType().Name, orderId);
                    throw new InvalidOperationException("Không tải được QR lên Cloudinary. Kiểm tra cấu hình/mạng rồi bấm Thử lại vé.", ex);
                }
                var url = uploaded.SecureUrl;
                if (uploaded.Error != null || url == null || url.Scheme != Uri.UriSchemeHttps || url.Host != "res.cloudinary.com"
                    || !url.AbsolutePath.StartsWith("/" + options.CloudName + "/", StringComparison.Ordinal)
                    || url.UserInfo.Length > 0 || url.AbsoluteUri.Length > 500)
                    throw new InvalidOperationException("Cloudinary chưa trả ảnh QR hợp lệ. Kiểm tra cấu hình rồi bấm Thử lại vé.");
                ticket.QrImageUrl = url.AbsoluteUri;
                await ctx.SaveChangesAsync(timeout.Token);
            }
        }
        _logger.LogInformation("Issued {TicketCount} tickets for order {OrderId}.", order.Tickets.Count, orderId);
        return order.Tickets.OrderBy(t => t.Id).ToList();
    }

    public string GenerateTicketCode(DateTime concertDate)
    {
        if (concertDate == default) throw new ArgumentException("Ngày concert không hợp lệ.", nameof(concertDate));
        using var ctx = _dbFactory.CreateDbContext();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string code = "CON-" + concertDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-"
                + RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 6);
            if (!ctx.Tickets.Any(t => t.TicketCode == code)) return code;
        }
        throw new InvalidOperationException("Chưa tạo được mã vé duy nhất. Vui lòng thử lại.");
    }
}

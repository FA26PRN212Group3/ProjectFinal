using ConcertTicket.Models;
using ConcertTicket.Options;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConcertTicket.Services;

//Người viết: Bảo (F5)
public class TicketService : ITicketService
{
    //chỉ đọc _options.Value bên trong hàm (lý do: xem GoogleAuthService)
    private readonly IOptions<CloudinaryOptions> _options;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly ILogger<TicketService> _logger;

    public TicketService(IOptions<CloudinaryOptions> options, IDbContextFactory<ConcertDbContext> dbFactory,
                         ILogger<TicketService> logger)
    {
        _options = options;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public Task<List<Ticket>> IssueTicketsAsync(int orderId)
    {
        //TODO (Bảo):
        //1. lấy Order (Include Zone, Concert, Tickets); đơn phải Paid
        //2. đã có vé thì trả luôn vé cũ
        //3. tạo Quantity vé: GenerateTicketCode, Gate = Zone.Gate
        //4. QRCoder tạo PNG chứa TicketCode, upload Cloudinary, lưu QrImageUrl
        throw new NotImplementedException("TODO (Bảo): IssueTicketsAsync");
    }

    public string GenerateTicketCode(DateTime concertDate)
    {
        //TODO (Bảo): "CON-" + concertDate.ToString("yyyyMMdd") + "-" + 6 ký tự ngẫu nhiên, kiểm không trùng
        throw new NotImplementedException("TODO (Bảo): GenerateTicketCode");
    }
}

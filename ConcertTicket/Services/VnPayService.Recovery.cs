using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConcertTicket.Models;
using ConcertTicket.Options;
using ConcertTicket.Services.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConcertTicket.Services;

public partial class VnPayService
{
    private readonly SemaphoreSlim _checkoutGate = new(1, 1);
    private static readonly string[] QueryRequestFields = ["vnp_RequestId", "vnp_Version", "vnp_Command", "vnp_TmnCode", "vnp_TxnRef", "vnp_TransactionDate", "vnp_CreateDate", "vnp_IpAddr", "vnp_OrderInfo"];
    private static readonly string[] QueryResponseFields = ["vnp_ResponseId", "vnp_Command", "vnp_ResponseCode", "vnp_Message", "vnp_TmnCode", "vnp_TxnRef", "vnp_Amount", "vnp_BankCode", "vnp_PayDate", "vnp_TransactionNo", "vnp_TransactionType", "vnp_TransactionStatus", "vnp_OrderInfo", "vnp_PromotionCode", "vnp_PromotionAmount"];

    private static DateTimeOffset ParseVietnamDate(string value) => new(
        DateTime.ParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None), TimeSpan.FromHours(7));

    private async Task<PaymentCheckout> GetOrCreateCheckoutAsync(Order order, VnPayOptions options, CancellationToken token)
    {
        await _checkoutGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
            PaymentCheckout? checkout = await ctx.PaymentCheckouts.AsNoTracking().SingleOrDefaultAsync(c => c.OrderId == order.Id, token).ConfigureAwait(false);
            if (checkout == null)
            {
                DateTimeOffset now = _time.GetUtcNow();
                checkout = new PaymentCheckout
                {
                    OrderId = order.Id, TxnRef = GetTransactionReference(order), TmnCode = options.TmnCode,
                    Amount = order.TotalAmount, CreateDate = FormatVietnamTimestamp(now),
                    ExpireDate = FormatVietnamTimestamp(now.AddMinutes(options.PaymentTimeoutMinutes)),
                    ReturnUrl = options.ReturnUrl, IpAddress = options.IpAddress
                };
                ctx.PaymentCheckouts.Add(checkout);
                ctx.PaymentActivities.Add(Activity(order.Id, "OrderCreated", "Success", "order:" + order.Id,
                    new DateTimeOffset(DateTime.SpecifyKind(order.CreatedAt, DateTimeKind.Unspecified), TimeSpan.FromHours(7))));
                ctx.PaymentActivities.Add(Activity(order.Id, "CheckoutInitialized", "Pending", "checkout:" + order.Id));
                ctx.PaymentActivities.Add(Activity(order.Id, "PaymentUrlGenerated", "Success", "url:" + order.Id));
                try
                {
                    await ctx.SaveChangesAsync(token).ConfigureAwait(false);
                }
                catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
                {
                    ctx.ChangeTracker.Clear();
                    checkout = await ctx.PaymentCheckouts.AsNoTracking().SingleAsync(c => c.OrderId == order.Id, token).ConfigureAwait(false);
                }
            }
            if (checkout.TxnRef != GetTransactionReference(order) || checkout.TmnCode != options.TmnCode
                || checkout.Amount != order.TotalAmount || checkout.ReturnUrl != options.ReturnUrl)
                throw new InvalidOperationException("Cấu hình hoặc đơn hàng đã đổi so với checkout gốc. Cần đối soát trước khi tiếp tục.");
            return checkout;
        }
        finally { _checkoutGate.Release(); }
    }

    public async Task<IReadOnlyList<PaymentActivity>> GetPaymentActivityAsync(int orderId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        List<PaymentActivity> entries = await ctx.PaymentActivities.AsNoTracking().Where(a => a.OrderId == orderId)
            .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        entries.Reverse();
        return entries;
    }

    public Task RecordPaymentPageOpenedAsync(int orderId, CancellationToken cancellationToken = default) =>
        RecordActivityAsync(orderId, "PaymentPageOpened", "Success", "page:" + orderId, cancellationToken);

    private PaymentActivity Activity(int? orderId, string type, string outcome, string key, DateTimeOffset? time = null) => new()
    {
        OrderId = orderId, EventType = type, Outcome = outcome, EventKey = key,
        OccurredAt = (time ?? _time.GetUtcNow()).ToUniversalTime(), Details = ActivityDescription(type)
    };

    private static string ActivityDescription(string type) => type switch
    {
        "OrderCreated" => "Đơn hàng được lưu trong hệ thống.",
        "CheckoutInitialized" => "Đã lưu thông tin checkout gốc để đối soát sau khi mở lại ứng dụng.",
        "PaymentUrlGenerated" => "Đã chuẩn bị yêu cầu thanh toán có chữ ký.",
        "PaymentPageOpened" => "WebView2 đã tải trang thanh toán.",
        "ReturnUrlReceived" => "Đã nhận chuyển hướng trình duyệt; chưa dùng để xác nhận thanh toán.",
        "IpnReceived" => "Đã nhận thông báo từ cổng thanh toán.",
        "HmacVerified" => "Chữ ký thông báo hợp lệ.",
        "HmacRejected" => "Thông báo không vượt qua kiểm tra xác thực.",
        "DatabaseTransactionStarted" => "Bắt đầu kiểm tra thanh toán và tồn kho trong transaction.",
        "PaymentConfirmed" => "Thanh toán và tồn kho đã được xác nhận đồng thời.",
        "PaymentFailed" => "Cổng thanh toán xác nhận giao dịch lỗi.",
        "DuplicateIpnIgnored" => "Thông báo lặp đã được nhận diện; không ghi nhận thanh toán lần nữa.",
        "TransactionRolledBack" => "Không hoàn tất được transaction; không xác nhận thay đổi thanh toán.",
        "QueryDrRequested" => "Đã gửi yêu cầu truy vấn kết quả giao dịch.",
        "QueryDrUnresolved" => "Chưa có kết quả cuối đã xác thực; giữ trạng thái hiện tại.",
        "QueryDrHttpError" => "Không truy vấn được kết quả do phản hồi HTTP từ cổng thanh toán.",
        "ReconciliationCompleted" => "Đã xử lý kết quả đối soát có chữ ký hợp lệ.",
        "ReconciliationRequired" => "Đã ghi nhận tiền nhưng không đủ tồn kho; cần xử lý thủ công, không thanh toán lại.",
        "TicketIssuanceReady" => "Đủ điều kiện chuyển đến chức năng phát hành vé.",
        _ => "Sự kiện thanh toán."
    };

    private async Task RecordActivityAsync(int? orderId, string type, string outcome, string key, CancellationToken token = default, string? details = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var ctx = await _dbFactory.CreateDbContextAsync(timeout.Token).ConfigureAwait(false);
            PaymentActivity entry = Activity(orderId, type, outcome, key);
            if (details != null) entry.Details = details;
            ctx.PaymentActivities.Add(entry);
            await ctx.SaveChangesAsync(timeout.Token).ConfigureAwait(false);
            _logger.LogInformation("Payment event {EventType} for order {OrderId}: {Outcome}.", type, orderId, outcome);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 }) { }
        catch (Exception exception)
        {
            _logger.LogWarning("Payment activity storage failed with {ErrorType}.", exception.GetType().Name);
        }
    }

    public async Task<PaymentResult> ReconcilePaymentAsync(int orderId, CancellationToken cancellationToken = default)
    {
        VnPayOptions options = GetValidatedOptions();
        Order? order = await ReadOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (order == null) return Result(orderId, PaymentState.Invalid, "Không tìm thấy đơn hàng.");
        PaymentResult current = DatabaseResult(order);
        if (order.Payment != null || order.Status != "Pending") return current;
        await using var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ctx.Database.SetCommandTimeout(10);
        PaymentCheckout? checkout = await ctx.PaymentCheckouts.AsNoTracking().SingleOrDefaultAsync(c => c.OrderId == orderId, cancellationToken).ConfigureAwait(false);
        if (checkout == null)
            return Result(orderId, PaymentState.AwaitingConfirmation, "Không có metadata checkout gốc để QueryDR. Không tự suy đoán ngày giao dịch; cần kiểm tra Merchant Admin.");
        if (!IsValidOrder(order) || checkout.TmnCode != options.TmnCode || checkout.TxnRef != GetTransactionReference(order)
            || checkout.Amount != order.TotalAmount || options.QueryTimeoutSeconds is < 1 or > 30)
            return Result(orderId, PaymentState.Invalid, "Thông tin checkout không khớp; không gửi yêu cầu đối soát.");
        if (_httpClients == null) throw new InvalidOperationException("Chưa đăng ký HttpClient cho VNPAY QueryDR.");

        DateTimeOffset now = _time.GetUtcNow();
        int claimed = await ctx.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE dbo.PaymentCheckout SET NextQueryAt={now.AddMinutes(5)}
            WHERE OrderId={orderId} AND (NextQueryAt IS NULL OR NextQueryAt<={now});", cancellationToken).ConfigureAwait(false);
        if (claimed != 1)
            return Result(orderId, PaymentState.AwaitingConfirmation, "Đối soát đang chạy hoặc đang trong giới hạn gọi lại 5 phút. Vẫn có thể tải trạng thái từ database.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.QueryTimeoutSeconds * 2 + 1));
        string correlation = Guid.NewGuid().ToString("N");
        string? failureMessage = null;
        try
        {
            using HttpClient client = _httpClients.CreateClient("VnPayQuery");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new Dictionary<string, string>
                {
                    ["vnp_RequestId"] = Guid.NewGuid().ToString("N"), ["vnp_Version"] = "2.1.0",
                    ["vnp_Command"] = "querydr", ["vnp_TmnCode"] = checkout.TmnCode,
                    ["vnp_TxnRef"] = checkout.TxnRef, ["vnp_TransactionDate"] = checkout.CreateDate,
                    ["vnp_CreateDate"] = FormatVietnamTimestamp(_time.GetUtcNow()), ["vnp_IpAddr"] = options.IpAddress,
                    ["vnp_OrderInfo"] = CreateOrderInformation(orderId)
                };
                request["vnp_SecureHash"] = CreateSignature(string.Join("|", QueryRequestFields.Select(f => request[f])), options.HashSecret);
                await RecordActivityAsync(orderId, "QueryDrRequested", "Pending", "query:" + correlation + ":" + attempt, deadline.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.QueryTimeoutSeconds));
                try
                {
                    using var message = new HttpRequestMessage(HttpMethod.Post, "https://sandbox.vnpayment.vn/merchant_webapi/api/transaction")
                    { Content = JsonContent.Create(request) };
                    using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        failureMessage = "VNPAY QueryDR trả HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                            + ". Chưa thể xác nhận thanh toán; không thanh toán lại.";
                        await RecordActivityAsync(orderId, "QueryDrHttpError", "Warning", "query-http:" + correlation + ":" + attempt,
                            deadline.Token, failureMessage).ConfigureAwait(false);
                    }
                    if ((int)response.StatusCode >= 500 && attempt == 0)
                    {
                        await Task.Delay(200, deadline.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (response.StatusCode != HttpStatusCode.OK) break;
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using var buffer = new MemoryStream();
                    byte[] chunk = new byte[4096];
                    int count;
                    while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0)
                    {
                        if (buffer.Length + count > 32_768) throw new InvalidDataException("Oversized QueryDR response");
                        await buffer.WriteAsync(chunk.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    }
                    Dictionary<string, string>? data = ValidateQueryResponse(buffer.ToArray(), checkout, options.HashSecret);
                    if (data == null)
                    {
                        await RecordActivityAsync(orderId, "HmacRejected", "Warning", "query-rejected:" + correlation, deadline.Token).ConfigureAwait(false);
                        break;
                    }
                    await RecordActivityAsync(orderId, "HmacVerified", "Success", "query-verified:" + correlation, deadline.Token).ConfigureAwait(false);
                    if (data["vnp_ResponseCode"] != "00") break;
                    if (data["vnp_TransactionStatus"] is not ("00" or "02")) break;
                    var confirmation = new Dictionary<string, string>(data, StringComparer.Ordinal)
                    {
                        ["vnp_ResponseCode"] = data["vnp_TransactionStatus"] == "00" ? "00" : "99",
                        ["vnp_OrderInfo"] = CreateOrderInformation(orderId)
                    };
                    if (!HasValidTransactionFields(confirmation) || !IsFinalIpnStatus(confirmation)) break;
                    VnPayIpnResponse processed = await ConfirmWithRetryAsync(orderId, confirmation, options.TmnCode, deadline.Token).ConfigureAwait(false);
                    await RecordActivityAsync(orderId, "ReconciliationCompleted", processed.RspCode is "00" or "02" ? "Success" : "Warning", "reconciled:" + correlation, deadline.Token).ConfigureAwait(false);
                    return await CheckPaymentStatusAsync(orderId, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt == 0) { }
                catch (OperationCanceledException) when (attempt == 0 && !deadline.IsCancellationRequested) { }
                if (attempt == 0) await Task.Delay(200, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning("VNPAY QueryDR failed with {ErrorType}.", exception.GetType().Name);
        }
        await RecordActivityAsync(orderId, "QueryDrUnresolved", "Warning", "unresolved:" + correlation, cancellationToken).ConfigureAwait(false);
        PaymentResult latest = await CheckPaymentStatusAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (latest.State is PaymentState.Pending or PaymentState.Expired or PaymentState.AwaitingConfirmation)
            return Result(orderId, PaymentState.AwaitingConfirmation, failureMessage ?? "Chưa xác minh được kết quả cuối qua QueryDR. Đơn vẫn chờ; không thanh toán lại.");
        return latest;
    }

    private static Dictionary<string, string>? ValidateQueryResponse(byte[] json, PaymentCheckout checkout, string secret)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            string? value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null
            };
            if (value == null || !data.TryAdd(property.Name, value)) return null;
        }
        if (!data.TryGetValue("vnp_SecureHash", out string? signature) || signature.Length != 128 || !signature.All(Uri.IsHexDigit)) return null;
        string expected = CreateSignature(string.Join("|", QueryResponseFields.Select(f => data.GetValueOrDefault(f, ""))), secret);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature), Convert.FromHexString(expected))) return null;
        if (!data.TryGetValue("vnp_ResponseId", out string? responseId) || responseId.Length is < 1 or > 32
            || !responseId.All(char.IsAsciiLetterOrDigit) || data.GetValueOrDefault("vnp_Command") != "querydr"
            || data.GetValueOrDefault("vnp_TmnCode") != checkout.TmnCode || data.GetValueOrDefault("vnp_TxnRef") != checkout.TxnRef
            || data.GetValueOrDefault("vnp_OrderInfo") != CreateOrderInformation(checkout.OrderId)
            || data.GetValueOrDefault("vnp_Amount") != (checkout.Amount * 100).ToString("0", CultureInfo.InvariantCulture)
            || data.GetValueOrDefault("vnp_TransactionType") != "01"
            || !TryNumeric(data, "vnp_ResponseCode", 2, 2, out _)
            || !TryNumeric(data, "vnp_TransactionStatus", 2, 2, out _)) return null;
        return data;
    }

    private async Task<VnPayIpnResponse> ConfirmWithRetryAsync(int orderId, Dictionary<string, string> parameters,
        string terminal, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await ConfirmTransactionAsync(orderId, parameters, terminal, token).ConfigureAwait(false); }
            catch (SqlException exception) when (exception.Number == 1205 && attempt < 2)
            { await Task.Delay(50 * (attempt + 1), token).ConfigureAwait(false); }
            catch
            {
                await RecordActivityAsync(orderId, "TransactionRolledBack", "Warning", "rollback:" + Guid.NewGuid().ToString("N")).ConfigureAwait(false);
                throw;
            }
        }
    }
}

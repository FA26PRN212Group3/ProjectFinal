using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ConcertTicket.Models;
using ConcertTicket.Options;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConcertTicket.Services;

public partial class VnPayService : IPaymentService
{
    private readonly IOptions<VnPayOptions> _options;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly ILogger<VnPayService> _logger;
    private readonly TimeProvider _time;
    private readonly IHttpClientFactory? _httpClients;
    private readonly object _attemptLock = new();
    private readonly Dictionary<int, PaymentAttempt> _attempts = [];

    public VnPayService(IOptions<VnPayOptions> options, IDbContextFactory<ConcertDbContext> dbFactory,
                        ILogger<VnPayService> logger, TimeProvider? timeProvider = null, IHttpClientFactory? httpClients = null)
    {
        _options = options;
        _dbFactory = dbFactory;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _httpClients = httpClients;
    }

    public string CreatePaymentUrl(Order order)
    {
        return CreatePaymentUrlAsync(order).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public bool IsReturnUrl(string url)
    {
        return IsSameReturnEndpoint(url, _options.Value.ReturnUrl);
    }

    public PaymentResult ProcessReturnUrl(string url)
    {
        return ProcessReturnUrlAsync(url).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public async Task<string> CreatePaymentUrlAsync(Order order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        VnPayOptions options = GetValidatedOptions();
        Order? saved = await ReadOrderAsync(order.Id, cancellationToken).ConfigureAwait(false);
        if (saved == null || saved.Status != "Pending" || saved.Payment != null)
            throw new InvalidOperationException("Đơn không còn khả dụng để khởi tạo thanh toán.");
        if (!IsValidOrder(saved))
            throw new InvalidOperationException("Thông tin hoặc số tiền của đơn không hợp lệ cho VNPAY.");

        PaymentCheckout checkout = await GetOrCreateCheckoutAsync(saved, options, cancellationToken).ConfigureAwait(false);
        DateTimeOffset expires = ParseVietnamDate(checkout.ExpireDate);
        if (expires <= _time.GetUtcNow())
            throw new InvalidOperationException("Phiên thanh toán đã hết hạn. Vui lòng đối soát, không thanh toán lại.");

        lock (_attemptLock)
        {
            if (_attempts.TryGetValue(saved.Id, out PaymentAttempt? existing))
            {
                if (existing.ReturnResult != null)
                    throw new InvalidOperationException("Đã nhận phản hồi thanh toán. Vui lòng kiểm tra xác nhận, không thanh toán lại.");
                if (existing.ExpiresAt <= _time.GetUtcNow())
                    throw new InvalidOperationException("Phiên thanh toán đã hết hạn. Vui lòng kiểm tra trạng thái trước khi đặt đơn khác.");
                if (existing.Amount != saved.TotalAmount || existing.Reference != GetTransactionReference(saved))
                    throw new InvalidOperationException("Thông tin đơn đã thay đổi. Không thể dùng lại phiên thanh toán.");
                return existing.Url!;
            }

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vnp_Version"] = "2.1.0",
                ["vnp_Command"] = "pay",
                ["vnp_TmnCode"] = options.TmnCode,
                ["vnp_Amount"] = FormatPaymentAmount(saved),
                ["vnp_CreateDate"] = checkout.CreateDate,
                ["vnp_ExpireDate"] = checkout.ExpireDate,
                ["vnp_CurrCode"] = "VND",
                ["vnp_IpAddr"] = checkout.IpAddress,
                ["vnp_Locale"] = "vn",
                ["vnp_OrderInfo"] = CreateOrderInformation(saved.Id),
                ["vnp_OrderType"] = "other",
                ["vnp_ReturnUrl"] = checkout.ReturnUrl,
                ["vnp_TxnRef"] = checkout.TxnRef
            };
            string query = BuildQueryString(parameters);
            string signature = CreateSignature(query, options.HashSecret);
            string url = options.BaseUrl + "?" + query + "&vnp_SecureHash=" + signature;
            _attempts.Add(saved.Id, new PaymentAttempt(url, saved.TotalAmount, parameters["vnp_TxnRef"], expires));
            return url;
        }
    }

    public async Task<PaymentResult> ProcessReturnUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        VnPayOptions options = GetValidatedOptions();
        if (!IsReturnUrl(url) || url.Length > 16_384) return InvalidReturn();
        var error = ReadNotification(new Uri(url).Query, options, out var data, out int orderId);
        if (error != null)
        {
            if (error.RspCode == "97")
                await RecordActivityAsync(null, "HmacRejected", "Warning", "return-rejected:" + Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
            return InvalidReturn();
        }

        Order? order = await ReadOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (order == null || !IsValidOrder(order)
            || data["vnp_TxnRef"] != GetTransactionReference(order)
            || data["vnp_Amount"] != FormatPaymentAmount(order)
            || data.GetValueOrDefault("vnp_OrderInfo") != CreateOrderInformation(order.Id))
            return InvalidReturn();

        string response = data["vnp_ResponseCode"];
        string returnKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data["vnp_SecureHash"])));
        await RecordActivityAsync(orderId, "ReturnUrlReceived", "Pending", "return:" + returnKey, cancellationToken).ConfigureAwait(false);
        string transactionNo = data["vnp_TransactionNo"];
        bool reportedSuccess = IsSuccessfulPayment(data);

        PaymentResult databaseResult = DatabaseResult(order);
        if (databaseResult.IsSuccess && (!reportedSuccess || order.Payment!.TransactionNo != transactionNo)) return InvalidReturn();

        lock (_attemptLock)
        {
            if (_attempts.TryGetValue(orderId, out PaymentAttempt? seen) && seen.ReturnFingerprint != null
                && seen.ReturnFingerprint != data["vnp_SecureHash"].ToUpperInvariant()) return InvalidReturn();
        }
        if (options.ConfirmFromSignedReturn && reportedSuccess && IsFinalIpnStatus(data))
        {
            await RecordActivityAsync(orderId, "HmacVerified", "Success", "return-verified:" + returnKey, cancellationToken).ConfigureAwait(false);
            if (order.Status == "Pending" && order.Payment == null)
            {
                VnPayIpnResponse confirmation = await ConfirmWithRetryAsync(orderId, data, options.TmnCode, cancellationToken).ConfigureAwait(false);
                if (confirmation.RspCode is not ("00" or "02"))
                    return Result(orderId, PaymentState.AwaitingConfirmation, "Chưa thể ghi nhận signed return. Vui lòng kiểm tra lại trạng thái đơn.");
                order = await ReadOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
                if (order == null) return InvalidReturn();
                databaseResult = DatabaseResult(order);
            }
            else if (order.Payment != null && IsSameProcessedIpn(order, order.Payment, data))
                await RecordActivityAsync(orderId, "DuplicateIpnIgnored", "Warning", "return-duplicate:" + returnKey, cancellationToken).ConfigureAwait(false);
        }

        lock (_attemptLock)
        {
            if (!_attempts.TryGetValue(order.Id, out PaymentAttempt? attempt))
            {
                attempt = new PaymentAttempt(null, order.TotalAmount, data["vnp_TxnRef"], DateTimeOffset.MinValue);
                _attempts.Add(order.Id, attempt);
            }
            string fingerprint = data["vnp_SecureHash"].ToUpperInvariant();
            if (attempt.ReturnFingerprint != null && attempt.ReturnFingerprint != fingerprint) return InvalidReturn();
            bool duplicate = attempt.ReturnFingerprint != null;
            PaymentResult result = databaseResult;
            if (databaseResult.State == PaymentState.Pending)
            {
                result = CreateBrowserReturnResult(order.Id, reportedSuccess, response);
            }
            result.IsVerified = true;
            result.IsDuplicate = duplicate;
            result.ResponseCode = response;
            result.TransactionNo = transactionNo;
            attempt.ReturnFingerprint = fingerprint;
            attempt.ReturnResult = Copy(result);
            if (attempt.ReturnResult.IsSuccess)
            {
                attempt.ReturnResult.IsSuccess = false;
                attempt.ReturnResult.State = PaymentState.AwaitingConfirmation;
                attempt.ReturnResult.Message = "Đang chờ xác nhận thanh toán trong hệ thống.";
            }
            if (!duplicate) _logger.LogInformation("VNPAY return verified for order {OrderId}; state={State}.", order.Id, result.State);
            return result;
        }
    }

    public async Task<PaymentResult> CheckPaymentStatusAsync(int orderId, CancellationToken cancellationToken = default)
    {
        Order? order = await ReadOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (order == null) return Result(orderId, PaymentState.Invalid, "Không tìm thấy đơn hàng.");
        PaymentResult result = DatabaseResult(order);
        if (result.State != PaymentState.Pending) return result;
        lock (_attemptLock)
        {
            if (_attempts.TryGetValue(orderId, out PaymentAttempt? attempt))
            {
                if (attempt.ReturnResult != null) return Copy(attempt.ReturnResult);
                if (attempt.ExpiresAt <= _time.GetUtcNow())
                    return Result(orderId, PaymentState.Expired, "Phiên thanh toán đã hết hạn. Vui lòng kiểm tra xác nhận trước khi đặt đơn khác.");
            }
        }
        await using var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        PaymentCheckout? checkout = await ctx.PaymentCheckouts.AsNoTracking().SingleOrDefaultAsync(c => c.OrderId == orderId, cancellationToken).ConfigureAwait(false);
        if (checkout != null && ParseVietnamDate(checkout.ExpireDate) <= _time.GetUtcNow())
            return Result(orderId, PaymentState.Expired, "Phiên thanh toán đã hết hạn. Vui lòng đối soát trước khi đặt đơn khác.");
        return result;
    }

    public static string GetTransactionReference(Order order)
    {
        if (order.Id <= 0 || order.CreatedAt == default)
        {
            throw new ArgumentException("Đơn chưa được lưu hợp lệ.", nameof(order));
        }

        string orderId = order.Id.ToString(CultureInfo.InvariantCulture);
        string createdAtTicks = order.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture);
        return "CT" + orderId + "T" + createdAtTicks;
    }

    public async Task<VnPayIpnResponse> ProcessIpnAsync(string queryString, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            VnPayOptions options = GetValidatedOptions();

            var error = ReadNotification(queryString, options, out var parameters, out int orderId);
            if (error != null)
            {
                if (error.RspCode == "97")
                    await RecordActivityAsync(null, "HmacRejected", "Warning", "rejected:" + Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
                return error;
            }
            string eventId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(parameters["vnp_SecureHash"])));
            await RecordActivityAsync(orderId, "IpnReceived", "Pending", "ipn:" + eventId, cancellationToken).ConfigureAwait(false);
            await RecordActivityAsync(orderId, "HmacVerified", "Success", "verified:" + eventId, cancellationToken).ConfigureAwait(false);
            if (!IsFinalIpnStatus(parameters))
                return IpnResponse("99", "Invalid or unresolved transaction status");

            VnPayIpnResponse processed = await ConfirmWithRetryAsync(orderId, parameters, options.TmnCode, cancellationToken).ConfigureAwait(false);
            if (processed.RspCode == "02")
                await RecordActivityAsync(orderId, "DuplicateIpnIgnored", "Warning", "duplicate:" + eventId, cancellationToken).ConfigureAwait(false);
            return processed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError("VNPAY IPN processing failed with {ErrorType}.", exception.GetType().Name);
        }

        return IpnResponse("99", "Unable to confirm transaction");
    }

    private async Task<VnPayIpnResponse> ConfirmTransactionAsync(int orderId, Dictionary<string, string> parameters,
        string terminalCode, CancellationToken cancellationToken)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ctx.Database.SetCommandTimeout(10);
        await using var transaction = await ctx.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await RecordActivityAsync(orderId, "DatabaseTransactionStarted", "Pending", "transaction:" + Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);

        string transactionNumber = parameters["vnp_TransactionNo"];
        if (transactionNumber.Any(character => character != '0'))
        {
            string lockName = "ConcertTicket.VnPay." + terminalCode + "." + transactionNumber;
            await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                DECLARE @LockResult int;
                EXEC @LockResult = sys.sp_getapplock @Resource={lockName},
                    @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=5000;
                IF @LockResult < 0 THROW 51001, 'Payment transaction lock unavailable.', 1;",
                cancellationToken).ConfigureAwait(false);
        }

        Order? order = await ctx.Orders.FromSqlInterpolated(
                $"SELECT * FROM dbo.[Order] WITH (UPDLOCK, HOLDLOCK) WHERE Id={orderId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (order == null || order.CreatedAt == default || parameters["vnp_TxnRef"] != GetTransactionReference(order))
        {
            return IpnResponse("01", "Order not found");
        }

        if (parameters.GetValueOrDefault("vnp_Amount") != FormatPaymentAmount(order))
        {
            return IpnResponse("04", "Invalid amount");
        }

        if (!IsValidOrder(order) || parameters.GetValueOrDefault("vnp_OrderInfo") != CreateOrderInformation(order.Id))
        {
            return IpnResponse("99", "Invalid order information");
        }

        Payment? existingPayment = await ctx.Payments.SingleOrDefaultAsync(p => p.OrderId == order.Id, cancellationToken).ConfigureAwait(false);
        if (existingPayment != null)
        {
            return IsSameProcessedIpn(order, existingPayment, parameters)
                ? IpnResponse("02", "Order already confirmed")
                : IpnResponse("99", "Conflicting payment requires reconciliation");
        }

        if (order.Status != "Pending" || order.PaidAt != null)
        {
            return IpnResponse("99", "Invalid order state");
        }

        if (transactionNumber.Any(character => character != '0'))
        {
            bool usedByAnotherOrder = await ctx.Payments.AsNoTracking().AnyAsync(
                p => p.Provider == "VNPay" && p.TransactionNo == transactionNumber && p.OrderId != orderId,
                cancellationToken).ConfigureAwait(false);
            if (usedByAnotherOrder)
            {
                return IpnResponse("99", "Transaction already belongs to another order");
            }
        }

        bool isSuccessful = parameters["vnp_ResponseCode"] == "00";
        if (isSuccessful)
        {
            int updatedZones = await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE dbo.Zone SET SoldQuantity=SoldQuantity+{order.Quantity}
                WHERE Id={order.ZoneId} AND SoldQuantity<=TotalQuantity-{order.Quantity};",
                cancellationToken).ConfigureAwait(false);

            if (updatedZones == 1)
            {
                order.Status = "Paid";
                order.PaidAt = DateTime.ParseExact(parameters["vnp_PayDate"], "yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            }
            else
            {
                _logger.LogWarning("VNPAY order {OrderId} requires inventory reconciliation.", order.Id);
            }
        }
        else
        {
            order.Status = "Failed";
        }

        ctx.Payments.Add(new Payment
        {
            OrderId = order.Id,
            Provider = "VNPay",
            Amount = order.TotalAmount,
            TransactionNo = transactionNumber,
            BankCode = parameters.GetValueOrDefault("vnp_BankCode"),
            ResponseCode = parameters["vnp_ResponseCode"]
        });

        string finalEvent = order.Status == "Paid" ? "PaymentConfirmed" : isSuccessful ? "ReconciliationRequired" : "PaymentFailed";
        ctx.PaymentActivities.Add(Activity(orderId, finalEvent, order.Status == "Paid" ? "Success" : isSuccessful ? "Warning" : "Failed", "settled:" + orderId));
        if (order.Status == "Paid")
            ctx.PaymentActivities.Add(Activity(orderId, "TicketIssuanceReady", "Success", "ticket-ready:" + orderId));
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Payment settlement committed for order {OrderId}: {Status}.", orderId, order.Status);
        return IpnResponse("00", "Confirm Success");
    }

    private static VnPayIpnResponse? ReadNotification(string queryString, VnPayOptions options,
        out Dictionary<string, string> parameters, out int orderId)
    {
        orderId = 0;
        if (!TryReadPaymentQuery(queryString, out parameters))
            return IpnResponse("99", "Invalid request");
        if (!HasValidSignature(parameters, options.HashSecret))
            return IpnResponse("97", "Invalid signature");
        if (parameters.GetValueOrDefault("vnp_TmnCode") != options.TmnCode)
            return IpnResponse("97", "Invalid merchant");
        if (!TryReadOrderId(parameters.GetValueOrDefault("vnp_TxnRef", ""), out orderId))
            return IpnResponse("01", "Order not found");
        if (!HasValidTransactionFields(parameters))
            return IpnResponse("99", "Invalid transaction fields");
        return null;
    }

    private static bool HasValidTransactionFields(Dictionary<string, string> parameters)
    {
        if (!TryNumeric(parameters, "vnp_Amount", 1, 12, out _)
            || !TryNumeric(parameters, "vnp_ResponseCode", 2, 2, out _)
            || !TryNumeric(parameters, "vnp_TransactionStatus", 2, 2, out _)
            || !TryNumeric(parameters, "vnp_TransactionNo", 1, 15, out string transactionNumber))
            return false;
        if (parameters.TryGetValue("vnp_CurrCode", out string? currency) && currency != "VND")
            return false;
        if (!IsSuccessfulPayment(parameters)) return true;
        return transactionNumber.Any(character => character != '0')
            && parameters.TryGetValue("vnp_PayDate", out string? paidAt)
            && DateTime.TryParseExact(paidAt, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool IsSuccessfulPayment(Dictionary<string, string> parameters) =>
        parameters["vnp_ResponseCode"] == "00" && parameters["vnp_TransactionStatus"] == "00";

    private static bool IsFinalIpnStatus(Dictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("vnp_BankCode", out string? bankCode)
            && (bankCode.Length > 20 || bankCode.Any(character => !char.IsAsciiLetterOrDigit(character))))
            return false;
        bool isDefinitiveFailure = parameters["vnp_ResponseCode"] != "00"
            && parameters["vnp_ResponseCode"] != "07" && parameters["vnp_TransactionStatus"] == "02";
        return IsSuccessfulPayment(parameters) || isDefinitiveFailure;
    }

    private static bool IsSameProcessedIpn(Order order, Payment payment, Dictionary<string, string> parameters)
    {
        bool matchesPayment = string.Equals(payment.Provider, "VNPay", StringComparison.OrdinalIgnoreCase)
            && payment.Amount == order.TotalAmount
            && payment.TransactionNo == parameters["vnp_TransactionNo"]
            && payment.ResponseCode == parameters["vnp_ResponseCode"];

        if (!matchesPayment)
        {
            return false;
        }

        if (payment.ResponseCode == "00")
        {
            return (order.Status == "Paid" && order.PaidAt != null)
                || (order.Status == "Pending" && order.PaidAt == null);
        }

        return order.Status == "Failed" && order.PaidAt == null;
    }

    private static bool TryReadOrderId(string reference, out int orderId)
    {
        orderId = 0;
        Match match = Regex.Match(reference, @"^CT([1-9][0-9]*)T([1-9][0-9]*)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out orderId);
    }

    private static VnPayIpnResponse IpnResponse(string responseCode, string message) =>
        new VnPayIpnResponse { RspCode = responseCode, Message = message };

    private async Task<Order?> ReadOrderAsync(int orderId, CancellationToken cancellationToken)
    {
        if (orderId <= 0) return null;
        await using var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Orders.AsNoTracking().Include(o => o.Payment)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken).ConfigureAwait(false);
    }

    private VnPayOptions GetValidatedOptions()
    {
        VnPayOptions options = _options.Value;

        bool hasValidTerminal = Regex.IsMatch(options.TmnCode ?? "", "^[A-Za-z0-9]{8}$", RegexOptions.CultureInvariant);
        bool hasValidSecret = !string.IsNullOrWhiteSpace(options.HashSecret)
            && options.HashSecret == options.HashSecret.Trim();
        bool hasValidGateway = IsValidGatewayUrl(options.BaseUrl);
        bool hasValidReturnUrl = IsValidConfiguredReturnUrl(options.ReturnUrl);
        bool hasValidTimeout = options.PaymentTimeoutMinutes >= 1 && options.PaymentTimeoutMinutes <= 60;
        bool hasValidIpAddress = IPAddress.TryParse(options.IpAddress, out _);

        if (!hasValidTerminal || !hasValidSecret || !hasValidGateway
            || !hasValidReturnUrl || !hasValidTimeout || !hasValidIpAddress)
        {
            throw new InvalidOperationException("Cấu hình VNPAY Sandbox chưa hợp lệ. Kiểm tra TmnCode, HashSecret, BaseUrl, ReturnUrl, thời hạn và IP.");
        }

        return options;
    }

    private static bool IsValidGatewayUrl(string url)
    {
        if (!TryCreateHttpUri(url, out Uri gateway))
        {
            return false;
        }

        return gateway.Scheme == "https"
            && gateway.IdnHost == "sandbox.vnpayment.vn"
            && gateway.Port == 443
            && gateway.AbsolutePath == "/paymentv2/vpcpay.html"
            && gateway.Query.Length == 0;
    }

    private static bool IsValidConfiguredReturnUrl(string url)
    {
        if (!TryCreateHttpUri(url, out Uri returnUri))
        {
            return false;
        }

        bool hasValidLength = url.Length <= 255;
        bool hasNoQuery = returnUri.Query.Length == 0;
        bool usesSecureConnection = returnUri.Scheme == "https" || returnUri.IsLoopback;

        return hasValidLength && hasNoQuery && usesSecureConnection;
    }

    private static bool IsValidOrder(Order order)
    {
        bool isSaved = order.Id > 0 && order.CreatedAt != default;
        bool hasValidQuantity = order.Quantity >= 1 && order.Quantity <= 4;
        bool hasValidPrice = order.UnitPrice > 0 && order.UnitPrice <= 9_999_999_999m
            && decimal.Truncate(order.UnitPrice) == order.UnitPrice;
        if (!isSaved || !hasValidQuantity || !hasValidPrice) return false;
        return order.TotalAmount == order.UnitPrice * order.Quantity
            && order.TotalAmount <= 9_999_999_999m;
    }

    private static string FormatPaymentAmount(Order order) =>
        (order.TotalAmount * 100).ToString("0", CultureInfo.InvariantCulture);

    private static string FormatVietnamTimestamp(DateTimeOffset value) =>
        value.ToOffset(TimeSpan.FromHours(7)).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private static string CreateOrderInformation(int orderId) =>
        "Thanh toan ve concert don hang " + orderId.ToString(CultureInfo.InvariantCulture);

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var sortedParameters = parameters.OrderBy(parameter => parameter.Key, StringComparer.Ordinal);
        var queryParts = new List<string>();

        foreach (var parameter in sortedParameters)
        {
            if (string.IsNullOrEmpty(parameter.Value))
            {
                continue;
            }

            if (parameter.Key == "vnp_SecureHash" || parameter.Key == "vnp_SecureHashType")
            {
                continue;
            }

            string encodedName = WebUtility.UrlEncode(parameter.Key);
            string encodedValue = WebUtility.UrlEncode(parameter.Value);
            queryParts.Add(encodedName + "=" + encodedValue);
        }

        return string.Join("&", queryParts);
    }

    private static string CreateSignature(string data, string secret)
    {
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        byte[] signatureBytes = HMACSHA512.HashData(secretBytes, dataBytes);
        return Convert.ToHexStringLower(signatureBytes);
    }

    private static bool HasValidSignature(Dictionary<string, string> parameters, string secret)
    {
        if (!parameters.TryGetValue("vnp_SecureHash", out string? receivedSignature))
        {
            return false;
        }

        if (receivedSignature.Length != 128 || !receivedSignature.All(Uri.IsHexDigit))
        {
            return false;
        }

        if (parameters.TryGetValue("vnp_SecureHashType", out string? hashType)
            && !string.Equals(hashType, "HmacSHA512", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string query = BuildQueryString(parameters);
        string expectedSignature = CreateSignature(query, secret);
        byte[] receivedBytes = Convert.FromHexString(receivedSignature);
        byte[] expectedBytes = Convert.FromHexString(expectedSignature);

        return CryptographicOperations.FixedTimeEquals(receivedBytes, expectedBytes);
    }

    private static bool TryCreateHttpUri(string value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsedUri)) return false;
        bool usesHttp = parsedUri.Scheme == Uri.UriSchemeHttp || parsedUri.Scheme == Uri.UriSchemeHttps;
        if (!usesHttp || parsedUri.UserInfo.Length > 0 || parsedUri.Fragment.Length > 0) return false;
        uri = parsedUri;
        return true;
    }

    private static bool IsSameReturnEndpoint(string url, string configuredReturnUrl)
    {
        if (!TryCreateHttpUri(url, out Uri incomingUri)
            || !TryCreateHttpUri(configuredReturnUrl, out Uri expectedUri) || expectedUri.Query.Length > 0)
            return false;
        return string.Equals(incomingUri.Scheme, expectedUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(incomingUri.IdnHost, expectedUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            && incomingUri.Port == expectedUri.Port
            && string.Equals(incomingUri.AbsolutePath, expectedUri.AbsolutePath, StringComparison.Ordinal);
    }

    private static bool TryReadPaymentQuery(string queryString, out Dictionary<string, string> parameters)
    {
        parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(queryString) || queryString.Length > 16_384
            || queryString[0] != '?' || queryString.Length < 2)
        {
            return false;
        }

        string query = queryString.Substring(1);
        foreach (string pair in query.Split('&'))
        {
            int separatorIndex = pair.IndexOf('=');
            if (separatorIndex < 1 || !HasValidUrlEscapes(pair))
            {
                return false;
            }

            string name = WebUtility.UrlDecode(pair.Substring(0, separatorIndex));
            string value = WebUtility.UrlDecode(pair.Substring(separatorIndex + 1));

            if (!name.StartsWith("vnp_", StringComparison.Ordinal))
            {
                return false;
            }

            if (!parameters.TryAdd(name, value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasValidUrlEscapes(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
                return false;
            index += 2;
        }
        return true;
    }

    private static PaymentResult CreateBrowserReturnResult(int orderId, bool reportedSuccess, string responseCode)
    {
        if (reportedSuccess)
        {
            return Result(orderId, PaymentState.AwaitingConfirmation,
                "Đã nhận kết quả từ VNPAY. Ứng dụng đang xác minh thanh toán.");
        }

        if (responseCode == "24")
        {
            return Result(orderId, PaymentState.Cancelled,
                "Bạn đã hủy thanh toán. Đơn vẫn chờ xác nhận; không tự động đánh dấu thất bại.");
        }

        return Result(orderId, PaymentState.Declined,
            "VNPAY trả về giao dịch không thành công. Đơn vẫn chờ xác nhận từ hệ thống.");
    }

    private static bool TryNumeric(Dictionary<string, string> data, string key, int minimum, int maximum, out string value)
    {
        value = data.GetValueOrDefault(key, "");
        return value.Length >= minimum && value.Length <= maximum
            && value.All(character => character >= '0' && character <= '9');
    }

    private static PaymentResult DatabaseResult(Order order)
    {
        Payment? payment = order.Payment;
        if (order.Status == "Paid")
        {
            if (HasConfirmedPayment(order))
            {
                return Result(order.Id, PaymentState.Confirmed, "Thanh toán đã được xác nhận trong hệ thống.", payment);
            }

            return Result(order.Id, PaymentState.AwaitingConfirmation, "Dữ liệu xác nhận thanh toán chưa đầy đủ. Vui lòng chờ đối soát.");
        }

        if (order.Status == "Failed")
        {
            return Result(order.Id, PaymentState.Declined, "Hệ thống đã xác nhận đơn thanh toán không thành công.", payment);
        }

        if (order.Status != "Pending")
        {
            return Result(order.Id, PaymentState.Invalid, "Trạng thái đơn không hợp lệ.");
        }

        if (payment != null)
        {
            if (payment.OrderId == order.Id && payment.Amount == order.TotalAmount
                && string.Equals(payment.Provider, "VNPay", StringComparison.OrdinalIgnoreCase)
                && payment.ResponseCode == "00")
            {
                return Result(order.Id, PaymentState.ReconciliationRequired,
                    "Thanh toán đã được ghi nhận nhưng chưa thể cấp vé. Cần đối soát giao dịch; vui lòng không thanh toán lại.", payment);
            }
            return Result(order.Id, PaymentState.AwaitingConfirmation, "Đang chờ hoàn tất xác nhận thanh toán trong hệ thống.");
        }

        return Result(order.Id, PaymentState.Pending, "Đơn hàng đang chờ thanh toán.");
    }

    private static bool HasConfirmedPayment(Order order)
    {
        Payment? payment = order.Payment;
        if (!IsValidOrder(order) || order.PaidAt == null || payment == null)
        {
            return false;
        }

        bool matchesOrder = payment.OrderId == order.Id && payment.Amount == order.TotalAmount;
        bool usesVnPay = string.Equals(payment.Provider, "VNPay", StringComparison.OrdinalIgnoreCase);
        bool hasSuccessResponse = payment.ResponseCode == "00";

        string? transactionNumber = payment.TransactionNo;
        bool hasValidTransactionNumber = !string.IsNullOrEmpty(transactionNumber)
            && transactionNumber.Length <= 15
            && transactionNumber.All(character => character >= '0' && character <= '9')
            && transactionNumber.Any(character => character != '0');

        return matchesOrder && usesVnPay && hasSuccessResponse && hasValidTransactionNumber;
    }

    private static PaymentResult InvalidReturn()
    {
        return Result(0, PaymentState.Invalid, "Không thể xác thực phản hồi thanh toán. Không có dữ liệu nào được cập nhật.");
    }

    private static PaymentResult Result(int orderId, PaymentState state, string message, Payment? payment = null)
    {
        return new PaymentResult
        {
            OrderId = orderId,
            State = state,
            IsSuccess = state == PaymentState.Confirmed,
            Message = message,
            ResponseCode = payment?.ResponseCode ?? "",
            TransactionNo = payment?.TransactionNo
        };
    }

    private static PaymentResult Copy(PaymentResult result)
    {
        return new PaymentResult
        {
            OrderId = result.OrderId,
            State = result.State,
            IsSuccess = result.IsSuccess,
            IsVerified = result.IsVerified,
            IsDuplicate = result.IsDuplicate,
            Message = result.Message,
            ResponseCode = result.ResponseCode,
            TransactionNo = result.TransactionNo
        };
    }

    private sealed class PaymentAttempt
    {
        public string? Url { get; }
        public decimal Amount { get; }
        public string Reference { get; }
        public DateTimeOffset ExpiresAt { get; }
        public string? ReturnFingerprint { get; set; }
        public PaymentResult? ReturnResult { get; set; }

        public PaymentAttempt(string? url, decimal amount, string reference, DateTimeOffset expiresAt)
        {
            Url = url;
            Amount = amount;
            Reference = reference;
            ExpiresAt = expiresAt;
        }
    }
}

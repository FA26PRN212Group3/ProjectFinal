using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ConcertTicket.Models;
using ConcertTicket.Options;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConcertTicket.Services;

public class VnPayService : IPaymentService
{
    private readonly IOptions<VnPayOptions> _options;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly ILogger<VnPayService> _logger;
    private readonly TimeProvider _time;
    private readonly object _attemptLock = new();
    private readonly Dictionary<int, PaymentAttempt> _attempts = [];

    public VnPayService(IOptions<VnPayOptions> options, IDbContextFactory<ConcertDbContext> dbFactory,
                        ILogger<VnPayService> logger, TimeProvider? timeProvider = null)
    {
        _options = options;
        _dbFactory = dbFactory;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
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
        ValidateOrder(saved);

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

            DateTimeOffset now = _time.GetUtcNow();
            DateTimeOffset expires = now.AddMinutes(options.PaymentTimeoutMinutes);
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vnp_Version"] = "2.1.0",
                ["vnp_Command"] = "pay",
                ["vnp_TmnCode"] = options.TmnCode,
                ["vnp_Amount"] = FormatPaymentAmount(saved),
                ["vnp_CreateDate"] = FormatVietnamTimestamp(now),
                ["vnp_ExpireDate"] = FormatVietnamTimestamp(expires),
                ["vnp_CurrCode"] = "VND",
                ["vnp_IpAddr"] = options.IpAddress,
                ["vnp_Locale"] = "vn",
                ["vnp_OrderInfo"] = CreateOrderInformation(saved.Id),
                ["vnp_OrderType"] = "other",
                ["vnp_ReturnUrl"] = options.ReturnUrl,
                ["vnp_TxnRef"] = GetTransactionReference(saved)
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
        if (!IsReturnUrl(url) || !TryReadReturnParameters(url, out var data)
            || !HasValidSignature(data, options.HashSecret)) return InvalidReturn();

        if (!data.TryGetValue("vnp_TmnCode", out string? terminal) || terminal != options.TmnCode
            || !data.TryGetValue("vnp_TxnRef", out string? reference)) return InvalidReturn();
        Match match = Regex.Match(reference, @"^CT([1-9][0-9]*)T([1-9][0-9]*)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int orderId))
            return InvalidReturn();

        Order? order = await ReadOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (order == null || !IsValidOrder(order)
            || reference != GetTransactionReference(order)
            || !data.TryGetValue("vnp_Amount", out string? amount) || amount != FormatPaymentAmount(order)
            || !data.TryGetValue("vnp_OrderInfo", out string? info) || info != CreateOrderInformation(order.Id)
            || !TryNumeric(data, "vnp_ResponseCode", 2, 2, out string response)
            || !TryNumeric(data, "vnp_TransactionStatus", 2, 2, out string transactionStatus)
            || !TryNumeric(data, "vnp_TransactionNo", 1, 15, out string transactionNo)
            || (data.TryGetValue("vnp_CurrCode", out string? currency) && currency != "VND")) return InvalidReturn();

        bool reportedSuccess = response == "00" && transactionStatus == "00";
        if (reportedSuccess && (transactionNo.All(c => c == '0')
            || !data.TryGetValue("vnp_PayDate", out string? paid)
            || !DateTime.TryParseExact(paid, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            return InvalidReturn();

        PaymentResult databaseResult = DatabaseResult(order);
        if (databaseResult.IsSuccess && (!reportedSuccess || order.Payment!.TransactionNo != transactionNo)) return InvalidReturn();

        lock (_attemptLock)
        {
            if (!_attempts.TryGetValue(order.Id, out PaymentAttempt? attempt))
            {
                attempt = new PaymentAttempt(null, order.TotalAmount, reference, DateTimeOffset.MinValue);
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
            if (!duplicate) _logger.LogInformation("VNPAY return verified for order {OrderId}; no database writes.", order.Id);
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
        if (order.Id <= 0 || order.CreatedAt == default)
        {
            return false;
        }

        if (order.Quantity < 1 || order.Quantity > 4)
        {
            return false;
        }

        if (order.UnitPrice <= 0 || order.UnitPrice > 9_999_999_999m)
        {
            return false;
        }

        if (decimal.Truncate(order.UnitPrice) != order.UnitPrice)
        {
            return false;
        }

        decimal expectedTotal = order.UnitPrice * order.Quantity;
        return order.TotalAmount == expectedTotal && order.TotalAmount <= 9_999_999_999m;
    }

    private static void ValidateOrder(Order order)
    {
        if (!IsValidOrder(order))
        {
            throw new InvalidOperationException("Thông tin hoặc số tiền của đơn không hợp lệ cho VNPAY.");
        }
    }

    private static string FormatPaymentAmount(Order order)
    {
        decimal amount = order.TotalAmount * 100;
        return amount.ToString("0", CultureInfo.InvariantCulture);
    }

    private static string FormatVietnamTimestamp(DateTimeOffset value)
    {
        DateTimeOffset vietnamTime = value.ToOffset(TimeSpan.FromHours(7));
        return vietnamTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    }

    private static string CreateOrderInformation(int orderId)
    {
        return "Thanh toan ve concert don hang " + orderId.ToString(CultureInfo.InvariantCulture);
    }

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
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsedUri))
        {
            return false;
        }

        bool usesHttp = parsedUri.Scheme == Uri.UriSchemeHttp || parsedUri.Scheme == Uri.UriSchemeHttps;
        bool hasUserInfo = parsedUri.UserInfo.Length > 0;
        bool hasFragment = parsedUri.Fragment.Length > 0;

        if (!usesHttp || hasUserInfo || hasFragment)
        {
            return false;
        }

        uri = parsedUri;
        return true;
    }

    private static bool IsSameReturnEndpoint(string url, string configuredReturnUrl)
    {
        if (!TryCreateHttpUri(url, out Uri incomingUri)
            || !TryCreateHttpUri(configuredReturnUrl, out Uri expectedUri))
        {
            return false;
        }

        if (expectedUri.Query.Length > 0)
        {
            return false;
        }

        bool sameScheme = string.Equals(incomingUri.Scheme, expectedUri.Scheme, StringComparison.OrdinalIgnoreCase);
        bool sameHost = string.Equals(incomingUri.IdnHost, expectedUri.IdnHost, StringComparison.OrdinalIgnoreCase);
        bool samePort = incomingUri.Port == expectedUri.Port;
        bool samePath = string.Equals(incomingUri.AbsolutePath, expectedUri.AbsolutePath, StringComparison.Ordinal);

        return sameScheme && sameHost && samePort && samePath;
    }

    private static bool TryReadReturnParameters(string url, out Dictionary<string, string> parameters)
    {
        parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        if (url.Length > 16_384 || !TryCreateHttpUri(url, out Uri uri) || uri.Query.Length < 2)
        {
            return false;
        }

        string query = uri.Query.Substring(1);
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
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length)
            {
                return false;
            }

            if (!Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static PaymentResult CreateBrowserReturnResult(int orderId, bool reportedSuccess, string responseCode)
    {
        if (reportedSuccess)
        {
            return Result(orderId, PaymentState.AwaitingConfirmation,
                "Đang chờ xác nhận thanh toán. ReturnUrl chưa phải xác nhận từ IPN.");
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

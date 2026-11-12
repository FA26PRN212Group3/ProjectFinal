namespace ConcertTicket.Services.Interfaces;

//kết quả IPaymentService.ProcessReturnUrl trả về
public class PaymentResult
{
    public int OrderId { get; set; }

    // Chỉ true khi Order và Payment trong DB đã xác nhận thành công.
    public bool IsSuccess { get; set; }

    public PaymentState State { get; set; } = PaymentState.Invalid;

    public bool IsVerified { get; set; }

    public bool IsDuplicate { get; set; }

    public string ResponseCode { get; set; } = "";   //'00' = thành công

    public string? TransactionNo { get; set; }

    public string Message { get; set; } = "";        //câu hiển thị cho user
}

public enum PaymentState
{
    Invalid,
    Pending,
    AwaitingConfirmation,
    Confirmed,
    Declined,
    Cancelled,
    Expired
}

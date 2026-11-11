namespace ConcertTicket.Services.Interfaces;

//kết quả IPaymentService.ProcessReturnUrl trả về
public class PaymentResult
{
    public int OrderId { get; set; }

    public bool IsSuccess { get; set; }

    public string ResponseCode { get; set; } = "";   //'00' = thành công

    public string? TransactionNo { get; set; }

    public string Message { get; set; } = "";        //câu hiển thị cho user
}

using ConcertTicket.Models;

namespace ConcertTicket.Services.Interfaces;

//Người viết: Dương
public interface IPaymentService
{
    //tạo URL VNPay Sandbox có chữ ký HMAC-SHA512 cho đơn Pending
    string CreatePaymentUrl(Order order);

    //WebView2 hỏi: URL sắp chuyển tới có phải ReturnUrl không?
    bool IsReturnUrl(string url);

    //kiểm chữ ký, đọc kết quả, rồi ghi trong 1 lần SaveChanges:
    //  thêm Payment + Order Paid/Failed + PaidAt + cộng Zone.SoldQuantity (nếu Paid)
    //chữ ký sai thì không ghi gì, trả IsSuccess = false
    PaymentResult ProcessReturnUrl(string url);
}

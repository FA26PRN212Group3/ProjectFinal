namespace ConcertTicket.Services.Interfaces;

//Người viết: Huy
//mọi Window mở nhau qua đây, KHÔNG tự new Window
public interface INavigationService
{
    void ShowLogin();

    void ShowMain();

    void ShowConcertDetail(int concertId);

    bool ShowPayment(int orderId);   //true = Order + Payment đã xác nhận trong DB, không dựa riêng ReturnUrl

    void ShowTicket(int orderId);
}

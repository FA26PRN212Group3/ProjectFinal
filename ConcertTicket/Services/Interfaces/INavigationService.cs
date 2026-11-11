namespace ConcertTicket.Services.Interfaces;

//Người viết: Huy
//mọi Window mở nhau qua đây, KHÔNG tự new Window
public interface INavigationService
{
    void ShowLogin();

    void ShowMain();

    void ShowConcertDetail(int concertId);

    bool ShowPayment(int orderId);   //true = VNPay báo thành công

    void ShowTicket(int orderId);
}

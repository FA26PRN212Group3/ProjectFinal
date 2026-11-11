using System.Windows;
using ConcertTicket.Services.Interfaces;
using ConcertTicket.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ConcertTicket.Services;

//Người viết: Huy
//Chỗ duy nhất (ngoài App.xaml.cs) được đụng tới IServiceProvider.
//Window nào cũng chỉ biết INavigationService, không tự new Window khác.
public class NavigationService : INavigationService
{
    private readonly IServiceProvider _provider;

    public NavigationService(IServiceProvider provider)
    {
        _provider = provider;
    }

    public void ShowLogin()
    {
        //Window không cần tham số => xin thẳng từ DI
        _provider.GetRequiredService<LoginWindow>().Show();
    }

    public void ShowMain()
    {
        _provider.GetRequiredService<MainWindow>().Show();
    }

    public void ShowConcertDetail(int concertId)
    {
        //Window cần tham số lúc chạy => ActivatorUtilities:
        //DI tự đưa các Service, mình đưa thêm concertId
        var win = ActivatorUtilities.CreateInstance<ConcertDetailWindow>(_provider, concertId);
        win.Owner = GetActiveWindow();
        win.ShowDialog();
    }

    public bool ShowPayment(int orderId)
    {
        var win = ActivatorUtilities.CreateInstance<PaymentWindow>(_provider, orderId);
        win.Owner = GetActiveWindow();
        return win.ShowDialog() == true;   //PaymentWindow đặt DialogResult = true khi thanh toán thành công
    }

    public void ShowTicket(int orderId)
    {
        var win = ActivatorUtilities.CreateInstance<TicketWindow>(_provider, orderId);
        win.Owner = GetActiveWindow();
        win.ShowDialog();
    }

    //cửa sổ đang mở phía trước, để cửa sổ mới hiện giữa nó
    private static Window? GetActiveWindow()
    {
        return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
    }
}

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
    private readonly Dictionary<int, PaymentWindow> _paymentWindows = [];

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
        if (_paymentWindows.TryGetValue(orderId, out PaymentWindow? opened))
        {
            opened.Activate();
            return false;
        }
        var win = ActivatorUtilities.CreateInstance<PaymentWindow>(_provider, orderId);
        win.Owner = GetActiveWindow();
        _paymentWindows.Add(orderId, win);
        try
        {
            return win.ShowDialog() == true; // Chỉ true sau khi đọc xác nhận Order + Payment trong DB.
        }
        finally { _paymentWindows.Remove(orderId); }
    }

    public void ShowTicket(int orderId)
    {
        var win = ActivatorUtilities.CreateInstance<TicketWindow>(_provider, orderId);
        win.Owner = GetActiveWindow();
        win.ShowDialog();
    }

    public void ShowCheckIn()
    {
        var win = _provider.GetRequiredService<CheckInWindow>();
        win.Owner = GetActiveWindow();
        win.ShowDialog();
    }

    //cửa sổ đang mở phía trước, để cửa sổ mới hiện giữa nó
    private static Window? GetActiveWindow()
    {
        return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
    }
}

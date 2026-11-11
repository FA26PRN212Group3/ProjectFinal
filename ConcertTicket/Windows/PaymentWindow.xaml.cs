using System.Windows;
using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ConcertTicket.Windows;

//Người làm: Dương (F4 - thanh toán VNPay)
public partial class PaymentWindow : Window
{
    private readonly IPaymentService _payment;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly int _orderId;

    public PaymentWindow(IPaymentService payment, IDbContextFactory<ConcertDbContext> dbFactory, int orderId)
    {
        InitializeComponent();
        _payment = payment;
        _dbFactory = dbFactory;
        _orderId = orderId;

        //TODO (Dương):
        //1. Loaded: lấy Order theo _orderId, url = _payment.CreatePaymentUrl(order), PaymentWebView.Source = new Uri(url)
        //2. PaymentWebView.NavigationStarting: nếu _payment.IsReturnUrl(e.Uri) thì e.Cancel = true,
        //   PaymentResult result = _payment.ProcessReturnUrl(e.Uri); MessageBox(result.Message);
        //   DialogResult = result.IsSuccess;   //đặt DialogResult là cửa sổ tự đóng
    }
}

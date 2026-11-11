using System.Windows;
using System.Windows.Input;
using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ConcertTicket.Windows;

//Người làm: Bảo (F2 - tìm kiếm)
public partial class MainWindow : Window
{
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;
    private readonly IUserSession _session;
    private readonly IAuthService _auth;
    private readonly INavigationService _nav;

    public MainWindow(IDbContextFactory<ConcertDbContext> dbFactory, IUserSession session,
                      IAuthService auth, INavigationService nav)
    {
        InitializeComponent();
        _dbFactory = dbFactory;
        _session = session;
        _auth = auth;
        _nav = nav;

        WelcomeTextBlock.Text = "Xin chào, " + _session.CurrentUser?.FullName;
        LoadConcerts();
    }

    private void LoadConcerts()
    {
        //mỗi lần cần thì tạo context mới, dùng xong tự đóng => dữ liệu luôn mới
        using var ctx = _dbFactory.CreateDbContext();

        //TODO (Bảo): ghép thêm điều kiện tìm kiếm bằng AsQueryable() + Where
        ConcertDataGrid.ItemsSource = ctx.Concerts.OrderBy(c => c.StartTime).ToList();
    }

    private void DetailButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedConcert();
    }

    private void ConcertDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedConcert();
    }

    private void OpenSelectedConcert()
    {
        Concert? concert = ConcertDataGrid.SelectedItem as Concert;
        if (concert == null)
        {
            MessageBox.Show("Hãy chọn 1 concert trước.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _nav.ShowConcertDetail(concert.Id);   //mở Window của Dương
        LoadConcerts();                       //quay lại thì tải lại
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        await _auth.LogoutAsync();
        _nav.ShowLogin();
        Close();
    }
}

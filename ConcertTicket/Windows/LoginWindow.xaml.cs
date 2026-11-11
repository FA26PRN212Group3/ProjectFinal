using System.Windows;
using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ConcertTicket.Windows;

//Người làm: Huy (F1)
public partial class LoginWindow : Window
{
    private readonly IAuthService _auth;
    private readonly IUserSession _session;
    private readonly INavigationService _nav;
    private readonly IDbContextFactory<ConcertDbContext> _dbFactory;

    //DI tự đưa 4 thứ này vào, Window không tự new gì cả
    public LoginWindow(IAuthService auth, IUserSession session, INavigationService nav,
                       IDbContextFactory<ConcertDbContext> dbFactory)
    {
        InitializeComponent();
        _auth = auth;
        _session = session;
        _nav = nav;
        _dbFactory = dbFactory;

#if !DEBUG
        DevLoginButton.Visibility = Visibility.Collapsed;   //bản Release không có nút test
#endif
    }

    private async void GoogleLoginButton_Click(object sender, RoutedEventArgs e)
    {
        GoogleLoginButton.IsEnabled = false;   //khóa nút, chống bấm 2 lần
        try
        {
            User? user = await _auth.LoginWithGoogleAsync();
            if (user == null)
            {
                MessageBox.Show("Bạn đã hủy đăng nhập.", "Đăng nhập", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _nav.ShowMain();
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Đăng nhập thất bại: " + ex.Message, "Đăng nhập", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            GoogleLoginButton.IsEnabled = true;
        }
    }

    //CHỈ ĐỂ TEST: Dương và Bảo vào được MainWindow khi Huy chưa làm xong Google login
    private void DevLoginButton_Click(object sender, RoutedEventArgs e)
    {
        using var ctx = _dbFactory.CreateDbContext();

        User? user = ctx.Users.FirstOrDefault(u => u.GoogleSub == "dev-local");
        if (user == null)
        {
            user = new User { GoogleSub = "dev-local", Email = "dev@local.test", FullName = "Tài khoản test" };
            ctx.Users.Add(user);
            ctx.SaveChanges();
        }

        _session.CurrentUser = user;   //không có GoogleCredential => chưa gọi được Calendar
        _nav.ShowMain();
        Close();
    }
}

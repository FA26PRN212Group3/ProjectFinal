using System.Windows;
using ConcertTicket.Models;
using ConcertTicket.Services.Interfaces;

namespace ConcertTicket.Windows;

public partial class LoginWindow : Window
{
    private readonly IAuthService _auth;
    private readonly INavigationService _nav;

    //DI tự đưa 2 thứ này vào, Window không tự new gì cả
    public LoginWindow(IAuthService auth, INavigationService nav)
    {
        InitializeComponent();
        _auth = auth;
        _nav = nav;
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
}

using System.Windows;
using System.Windows.Threading;
using System.Net.Http;
using ConcertTicket.Models;
using ConcertTicket.Options;
using ConcertTicket.Services;
using ConcertTicket.Services.Interfaces;
using ConcertTicket.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConcertTicket;

//COMPOSITION ROOT: chỗ DUY NHẤT tạo và ráp mọi object của app.
//Class nào cần gì thì khai trong constructor, DI tự đưa vào.
//Thêm class mới => nhớ đăng ký ở đây, quên thì lúc mở sẽ báo "Unable to resolve service...".
public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        //Host tự đọc appsettings.json + user-secrets (khi DOTNET_ENVIRONMENT=Development) + bật logging
        //ContentRootPath = thư mục chứa file .exe, nơi có appsettings.json
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory
        });

        //===== Options: đọc từng mục cấu hình thành class =====
        //Validate chạy lúc Service đọc .Value lần đầu => thiếu key thì báo đúng lúc dùng tới
        builder.Services.AddOptions<GoogleOptions>()
            .Bind(builder.Configuration.GetSection("Google"))
            .Validate(o => o.ClientId != "" && o.ClientSecret != "", "Thiếu cấu hình Google (ClientId/ClientSecret).");
        builder.Services.AddOptions<VnPayOptions>()
            .Bind(builder.Configuration.GetSection("VnPay"))
            .Validate(o => o.TmnCode != "" && o.HashSecret != "", "Thiếu cấu hình VNPay (TmnCode/HashSecret).");
        builder.Services.AddOptions<VnPayIpnOptions>()
            .Bind(builder.Configuration.GetSection("VnPayIpn"));
        builder.Services.AddOptions<CloudinaryOptions>()
            .Bind(builder.Configuration.GetSection("Cloudinary"))
            .Validate(o => o.CloudName != "" && o.ApiKey != "" && o.ApiSecret != "", "Thiếu cấu hình Cloudinary.");

        //===== Database: "nhà máy" tạo DbContext, mỗi thao tác xin 1 cái mới =====
        builder.Services.AddDbContextFactory<ConcertDbContext>(o =>
            o.UseSqlServer(builder.Configuration.GetConnectionString("ConcertDb")));

        //===== Services: cả app dùng chung 1 object (Singleton) =====
        builder.Services.AddSingleton<IUserSession, UserSession>();
        builder.Services.AddSingleton<IAuthService, GoogleAuthService>();
        builder.Services.AddSingleton<INavigationService, NavigationService>();
        builder.Services.AddSingleton<IPaymentService, VnPayService>();
        builder.Services.AddHttpClient("VnPayQuery", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ConcertTicket/1.0");
        })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddHostedService<VnPayIpnListener>();
        builder.Services.AddSingleton<ITicketService, TicketService>();
        builder.Services.AddSingleton<ICalendarService, GoogleCalendarService>();

        //===== Window không cần tham số: mỗi lần xin là cửa sổ mới (Transient) =====
        //ConcertDetailWindow, PaymentWindow, TicketWindow cần tham số => NavigationService tạo bằng ActivatorUtilities
        builder.Services.AddTransient<LoginWindow>();
        builder.Services.AddTransient<MainWindow>();

        _host = builder.Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        //lỗi nào chưa bắt (kể cả trong hàm async void Click) => ghi log + báo, app không tắt ngang
        DispatcherUnhandledException += App_DispatcherUnhandledException;

        try
        {
            await _host.StartAsync();
        }
        catch (Exception exception)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogError("Application startup failed with {ErrorType}.", exception.GetType().Name);
            MessageBox.Show("Không thể khởi động ứng dụng. Kiểm tra cổng và cấu hình VNPAY IPN, sau đó mở lại ứng dụng.",
                "Lỗi khởi động", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        //chỗ DUY NHẤT trong app gọi GetRequiredService để mở cửa sổ đầu tiên
        _host.Services.GetRequiredService<INavigationService>().ShowLogin();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            _host.StopAsync(timeout.Token).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogError("Application shutdown failed with {ErrorType}.", exception.GetType().Name);
        }
        finally
        {
            _host.Dispose();
            base.OnExit(e);
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host.Services.GetRequiredService<ILogger<App>>().LogError(e.Exception, "Lỗi chưa xử lý");
        MessageBox.Show(e.Exception.Message, "Có lỗi xảy ra", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

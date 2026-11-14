using System.Windows;
using System.Windows.Media;
using ConcertTicket.Services.Interfaces;
using Microsoft.Win32;

namespace ConcertTicket.Windows;

//Người làm: Huy (check-in - soát vé bằng mã QR)
public partial class CheckInWindow : Window
{
    private readonly ITicketService _ticketService;
    private readonly ICalendarService? _calendar;
    private bool _checking;

    public CheckInWindow(ITicketService ticketService, ICalendarService? calendar = null)
    {
        InitializeComponent();
        _ticketService = ticketService;
        _calendar = calendar;
        Loaded += (_, _) => CodeTextBox.Focus();
    }

    //máy quét QR dạng USB gõ mã vào TextBox rồi bấm Enter => IsDefault gọi nút này
    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckInAsync(CodeTextBox.Text);
    }

    private async void PickImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn ảnh mã QR",
            Filter = "Ảnh (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (dialog.ShowDialog(this) != true) return;

        string? code;
        try
        {
            code = _ticketService.ReadQrCode(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowResult("#F8D7DA", "Không đọc được ảnh", ex.Message);
            return;
        }
        if (code == null)
        {
            ShowResult("#F8D7DA", "Không tìm thấy mã QR", "Ảnh không có mã QR hoặc bị mờ, hãy chọn ảnh khác.");
            return;
        }
        CodeTextBox.Text = code;
        await CheckInAsync(code);
    }

    private async Task CheckInAsync(string code)
    {
        if (_checking) return;
        if (code.Trim() == "")
        {
            ShowResult("#EEEEEE", "Chờ quét vé...", "Hãy nhập hoặc quét mã vé.");
            CodeTextBox.Focus();
            return;
        }

        _checking = true;
        CheckButton.IsEnabled = PickImageButton.IsEnabled = false;
        try
        {
            CheckInResult result = await _ticketService.CheckInAsync(code);
            string detail = "";
            if (result.Ticket != null)
            {
                var ticket = result.Ticket;
                detail = ticket.TicketCode + "\n"
                    + ticket.Order.Zone.Concert.Name + "\n"
                    + "Khu: " + ticket.Order.Zone.Name + "  -  Cổng: " + ticket.Gate;
            }

            if (result.Result == "OK")
            {
                ShowResult("#D4EDDA", "✔ CHECK-IN THÀNH CÔNG", detail);
                if (result.Ticket != null && _calendar != null)
                {
                    try
                    {
                        await _calendar.RemoveOrderEventAsync(result.Ticket.OrderId);
                    }
                    catch (Exception ex)
                    {
                        DetailTextBlock.Text += "\n(Chưa xóa được lịch Google: " + ex.Message + ")";
                    }
                }
            }
            else if (result.Result == "Used")
                ShowResult("#FFF3CD", "⚠ VÉ ĐÃ CHECK-IN RỒI", detail);
            else
                ShowResult("#F8D7DA", "✖ VÉ KHÔNG HỢP LỆ", "Không có vé nào mang mã: " + code.Trim().ToUpperInvariant());
        }
        catch (Exception ex)
        {
            ShowResult("#F8D7DA", "Lỗi khi soát vé", ex.Message);
        }
        finally
        {
            _checking = false;
            CheckButton.IsEnabled = PickImageButton.IsEnabled = true;
            //xóa ô mã để quét vé tiếp theo
            CodeTextBox.Clear();
            CodeTextBox.Focus();
        }
    }

    private void ShowResult(string color, string title, string detail)
    {
        ResultBorder.Background = (Brush)new BrushConverter().ConvertFromString(color)!;
        ResultTextBlock.Text = title;
        DetailTextBlock.Text = detail;
    }
}

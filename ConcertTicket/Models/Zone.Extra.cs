namespace ConcertTicket.Models;

//Phần thêm cho class Zone (scaffold sinh ra Zone.cs là partial class).
//Viết ở file riêng để scaffold lại không bị mất.
//Property chỉ có get => EF Core KHÔNG map xuống database, chỉ dùng để Binding lên DataGrid.
public partial class Zone
{
    public int Remaining => TotalQuantity - SoldQuantity;

    public bool IsSoldOut => Remaining <= 0;
}

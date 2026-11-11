namespace ConcertTicket.Options;

//đọc từ mục "Google" trong appsettings.json / user-secrets
public class GoogleOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

namespace ConcertTicket.Options;

//đọc từ mục "Cloudinary" trong appsettings.json / user-secrets
public class CloudinaryOptions
{
    public string CloudName { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string Folder { get; set; } = "concert-tickets";
}

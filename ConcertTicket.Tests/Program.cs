using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConcertTicket.Models;
using ConcertTicket.Services;
using ConcertTicket.Services.Interfaces;
using ConcertTicket.Windows;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var task = RunAsync();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
        Dispatcher.PushFrame(frame);
        try
        {
            task.GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {_passed} calendar tests; no Google requests or application data changed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        var databaseName = "ConcertTicketCalendarTests_" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<ConcertDbContext>().UseSqlServer(
            $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True;Connect Timeout=30").Options;
        var factory = new TestDbFactory(options);
        using var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = "calendar-test", ClientSecret = "not-a-real-secret" },
            Scopes = ["https://www.googleapis.com/auth/calendar.events"]
        });
        var google = new OfflineGoogle();
        var credential = new OfflineCredential(flow, google);
        var session = new UserSession { GoogleCredential = credential };
        var service = new GoogleCalendarService(session, factory, NullLogger<GoogleCalendarService>.Instance);
        int zoneId;
        int otherUserId;

        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                var user = new User { GoogleSub = "calendar-owner", Email = "owner@example.test", FullName = "Calendar Owner" };
                var other = new User { GoogleSub = "calendar-other", Email = "other@example.test", FullName = "Other User" };
                var zone = new Zone
                {
                    Name = "<b>VIP & Friends</b>", Gate = "Gate A", Price = 100000, TotalQuantity = 1000,
                    Concert = new Concert
                    {
                        Name = "Đêm nhạc Calendar", Artist = "Test Artist", Venue = "Nhà hát", Address = "TP.HCM",
                        StartTime = new DateTime(2030, 12, 20, 19, 30, 0), EndTime = new DateTime(2030, 12, 20, 22, 0, 0)
                    }
                };
                db.AddRange(user, other, zone);
                await db.SaveChangesAsync();
                session.CurrentUser = user;
                zoneId = zone.Id;
                otherUserId = other.Id;
            }

            async Task<Order> NewOrder(string status = "Paid", int tickets = 2, int? userId = null)
            {
                await using var db = factory.CreateDbContext();
                var order = new Order
                {
                    UserId = userId ?? session.CurrentUser!.Id, ZoneId = zoneId, Quantity = 2,
                    UnitPrice = 100000, TotalAmount = 200000, Status = status,
                    CreatedAt = new DateTime(2026, 11, 12, 12, 0, 0),
                    PaidAt = status == "Paid" ? new DateTime(2026, 11, 12, 12, 5, 0) : null
                };
                for (var i = 0; i < tickets; i++) order.Tickets.Add(NewTicket());
                db.Orders.Add(order);
                await db.SaveChangesAsync();
                return order;
            }

            async Task<Order> ReadOrder(int id)
            {
                await using var db = factory.CreateDbContext();
                return await db.Orders.AsNoTracking().Include(o => o.Tickets).SingleAsync(o => o.Id == id);
            }

            var first = await NewOrder();
            string? firstLink = null;
            await Test("event content, Vietnam time, reminder, HTML escaping and database persistence", async () =>
            {
                firstLink = await service.AddOrderEventAsync(first.Id);
                var saved = await ReadOrder(first.Id);
                Assert(saved.GoogleEventId is { Length: 64 } && saved.GoogleEventId.All(c => char.IsAsciiHexDigit(c)), "stable Google event ID");
                Assert(saved.GoogleEventLink == firstLink, "event link persisted");
                Assert(saved.Status == first.Status && saved.PaidAt == first.PaidAt && saved.Tickets.Count == 2, "payment and tickets unchanged");
                var body = google.Events[saved.GoogleEventId!];
                Assert(body["summary"]!.GetValue<string>() == "Đêm nhạc Calendar", "concert name");
                Assert(body["location"]!.GetValue<string>() == "Nhà hát, TP.HCM", "venue and address");
                var start = DateTimeOffset.Parse(body["start"]!["dateTime"]!.GetValue<string>());
                var end = DateTimeOffset.Parse(body["end"]!["dateTime"]!.GetValue<string>());
                Assert(start.Offset == TimeSpan.FromHours(7) && start.Hour == 19 && start.Minute == 30 && end.Hour == 22, "UTC+7 times");
                Assert(body["start"]!["timeZone"]!.GetValue<string>() == "Asia/Ho_Chi_Minh", "IANA time zone");
                Assert(body["reminders"]!["useDefault"]!.GetValue<bool>() == false &&
                    body["reminders"]!["overrides"]![0]!["minutes"]!.GetValue<int>() == 1440 &&
                    body["reminders"]!["overrides"]![0]!["method"]!.GetValue<string>() == "popup", "one-day popup");
                var description = body["description"]!.GetValue<string>();
                Assert(description.Contains("&lt;b&gt;VIP &amp; Friends&lt;/b&gt;") && !description.Contains("<b>"), "untrusted HTML encoded");
                Assert(saved.Tickets.All(t => description.Contains(t.TicketCode) && description.Contains(t.QrImageUrl!)) && description.Contains("Gate A"), "all ticket codes, gates and QR links");
            });

            await Test("reopening a saved event makes no HTTP request", async () =>
            {
                var calls = google.Requests;
                Assert(await service.AddOrderEventAsync(first.Id) == firstLink && google.Requests == calls, "reuse saved link");
            });

            await Test("missing saved link recovers the same Google event", async () =>
            {
                await using (var db = factory.CreateDbContext())
                {
                    var order = await db.Orders.FindAsync(first.Id);
                    order!.GoogleEventLink = null;
                    await db.SaveChangesAsync();
                }
                var count = google.Events.Count;
                Assert(await service.AddOrderEventAsync(first.Id) == firstLink && google.Events.Count == count, "409/get recovery");
            });

            await Test("invalid IDs and unauthenticated sessions are rejected before HTTP", async () =>
            {
                var calls = google.Requests;
                await Throws<ArgumentOutOfRangeException>(() => service.AddOrderEventAsync(0));
                await Throws<InvalidOperationException>(() => service.AddOrderEventAsync(int.MaxValue));
                var user = session.CurrentUser;
                session.CurrentUser = null;
                await Throws<InvalidOperationException>(() => service.AddOrderEventAsync(first.Id));
                session.CurrentUser = user;
                session.GoogleCredential = null;
                await Throws<InvalidOperationException>(() => service.AddOrderEventAsync(first.Id));
                session.GoogleCredential = credential;
                Assert(google.Requests == calls, "no unauthenticated API call");
            });

            await Test("other users, unpaid orders and incomplete tickets are rejected", async () =>
            {
                var calls = google.Requests;
                var foreign = await NewOrder(userId: otherUserId);
                await Throws<InvalidOperationException>(() => service.AddOrderEventAsync(foreign.Id));
                foreach (var status in new[] { "Pending", "Failed" })
                {
                    var unpaid = await NewOrder(status);
                    await Throws<InvalidOperationException>(() => service.AddOrderEventAsync(unpaid.Id));
                }
                foreach (var count in new[] { 0, 1 })
                {
                    var incomplete = await NewOrder(tickets: count);
                    await Throws<InvalidOperationException>(() => service.AddOrderEventAsync(incomplete.Id));
                }
                Assert(google.Requests == calls, "invalid orders never reach Google");
            });

            await Test("Google permission failure does not modify the order", async () =>
            {
                var order = await NewOrder();
                google.NextError = HttpStatusCode.Forbidden;
                await Throws<GoogleApiException>(() => service.AddOrderEventAsync(order.Id));
                var saved = await ReadOrder(order.Id);
                Assert(saved.GoogleEventId == null && saved.GoogleEventLink == null && saved.Status == "Paid", "no partial metadata");
            });

            await Test("retry after database failure recovers without duplicating the event", async () =>
            {
                var order = await NewOrder();
                var count = google.Events.Count;
                factory.FailNextSave = true;
                await Throws<InvalidOperationException>(() => service.AddOrderEventAsync(order.Id));
                Assert((await ReadOrder(order.Id)).GoogleEventId == null && google.Events.Count == count + 1, "Google succeeded, DB failed");
                await service.AddOrderEventAsync(order.Id);
                Assert((await ReadOrder(order.Id)).GoogleEventId != null && google.Events.Count == count + 1, "retry repairs DB only");
            });

            await Test("concurrent calls create one event per order", async () =>
            {
                var order = await NewOrder();
                var count = google.Events.Count;
                var links = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.AddOrderEventAsync(order.Id)));
                Assert(links.Distinct().Count() == 1 && google.Events.Count == count + 1, "one event across four concurrent calls");
            });

            await Test("unsafe event links are rejected and can be recovered", async () =>
            {
                var order = await NewOrder();
                google.InvalidLinkOnce = true;
                await Throws<InvalidOperationException>(() => service.AddOrderEventAsync(order.Id));
                Assert((await ReadOrder(order.Id)).GoogleEventLink == null, "file URL not persisted");
                var count = google.Events.Count;
                Assert((await service.AddOrderEventAsync(order.Id)).StartsWith("https://calendar.google.com/", StringComparison.Ordinal) &&
                    google.Events.Count == count, "retry reuses existing event");
            });

            await Test("ticket window stays busy until Calendar completes, then enables opening", async () =>
            {
                var order = await NewOrder();
                var calls = google.Requests;
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                google.InsertGate = release.Task;
                var window = new TicketWindow(new TestTicketService(), service, factory, session, order.Id);
                try
                {
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    await Until(() => google.Requests > calls);
                    Assert(!Control<Button>(window, "OpenCalendarButton").IsEnabled &&
                        !Control<Button>(window, "RetryCalendarButton").IsEnabled, "buttons disabled in flight");
                    release.SetResult();
                    await Until(() => Control<Button>(window, "OpenCalendarButton").IsEnabled);
                    Assert(Control<ListBox>(window, "TicketListBox").Items.Count == 2, "existing tickets displayed without reissuing");
                    Assert(Control<TextBlock>(window, "CalendarStatusTextBlock").Text.Contains("1 ngày"), "success message");
                    Render(window, "calendar-success.png");
                    calls = google.Requests;
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Assert(google.Requests == calls, "Loaded handler runs only once");
                }
                finally { release.TrySetResult(); google.InsertGate = null; window.Close(); }
            });

            await Test("ticket window preserves tickets on Calendar failure and supports retry", async () =>
            {
                var order = await NewOrder();
                google.NextError = HttpStatusCode.Forbidden;
                var window = new TicketWindow(new TestTicketService(), service, factory, session, order.Id);
                try
                {
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    var retry = Control<Button>(window, "RetryCalendarButton");
                    await Until(() => retry.IsEnabled);
                    Assert(retry.Visibility == Visibility.Visible && !Control<Button>(window, "OpenCalendarButton").IsEnabled, "retry available after failure");
                    Assert(Control<ListBox>(window, "TicketListBox").Items.Count == 2 &&
                        Control<TextBlock>(window, "CalendarStatusTextBlock").Text.Contains("quyền"), "tickets intact, permission guidance shown");
                    Render(window, "calendar-retry.png");
                    retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => Control<Button>(window, "OpenCalendarButton").IsEnabled);
                    Assert(retry.Visibility == Visibility.Collapsed && (await ReadOrder(order.Id)).GoogleEventId != null, "retry completed and persisted");
                }
                finally { window.Close(); }
            });

            await Test("Calendar runs after ticket issuance finishes", async () =>
            {
                var order = await NewOrder(tickets: 0);
                var issued = false;
                var tickets = new TestTicketService(async id =>
                {
                    Assert(id == order.Id, "correct order issued");
                    await using var db = factory.CreateDbContext();
                    var saved = await db.Orders.Include(o => o.Tickets).SingleAsync(o => o.Id == id);
                    saved.Tickets.Add(NewTicket());
                    saved.Tickets.Add(NewTicket());
                    await db.SaveChangesAsync();
                    issued = true;
                    return saved.Tickets.ToList();
                });
                var window = new TicketWindow(tickets, service, factory, session, order.Id);
                try
                {
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    await Until(() => Control<Button>(window, "OpenCalendarButton").IsEnabled);
                    Assert(issued && (await ReadOrder(order.Id)).GoogleEventId != null, "issuance precedes Calendar");
                }
                finally { window.Close(); }
            });

            await Test("ticket window blocks foreign orders before displaying tickets or calling Calendar", async () =>
            {
                var order = await NewOrder(userId: otherUserId);
                var calls = google.Requests;
                var window = new TicketWindow(new TestTicketService(), service, factory, session, order.Id);
                try
                {
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    await Until(() => Control<TextBlock>(window, "StatusTextBlock").Text.StartsWith("Không tải được vé:", StringComparison.Ordinal));
                    Assert(Control<ListBox>(window, "TicketListBox").Items.Count == 0 && google.Requests == calls, "no data leak or Calendar call");
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            await using var db = factory.CreateDbContext();
            Assert(db.Database.GetDbConnection().Database == databaseName && databaseName.StartsWith("ConcertTicketCalendarTests_", StringComparison.Ordinal), "cleanup targets only this test database");
            await db.Database.EnsureDeletedAsync();
            Console.WriteLine("Removed temporary test database: " + databaseName);
        }
    }

    private static Ticket NewTicket()
    {
        var code = "CAL-" + Guid.NewGuid().ToString("N")[..20];
        return new Ticket { TicketCode = code, Gate = "Gate A", Status = "Valid", QrImageUrl = $"https://res.cloudinary.com/calendar-test/image/upload/{code}.png" };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task Test(string name, Func<Task> action)
    {
        await action();
        Console.WriteLine("PASS " + name);
        _passed++;
    }

    private static T Control<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static void Render(Window window, string filename)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(688, 488));
        root.Arrange(new Rect(0, 0, 688, 488));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(688, 488, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, 688, 488));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, filename));
        encoder.Save(file);
    }
}

internal sealed class TestDbFactory(DbContextOptions<ConcertDbContext> options) : IDbContextFactory<ConcertDbContext>
{
    public bool FailNextSave { get; set; }
    public ConcertDbContext CreateDbContext() => new TestDbContext(options, this);
}

internal sealed class TestDbContext(DbContextOptions<ConcertDbContext> options, TestDbFactory factory) : ConcertDbContext(options)
{
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!factory.FailNextSave) return base.SaveChangesAsync(cancellationToken);
        factory.FailNextSave = false;
        throw new InvalidOperationException("Simulated database write failure");
    }
}

internal sealed class OfflineCredential(IAuthorizationCodeFlow flow, OfflineGoogle google)
    : UserCredential(flow, "calendar-test", new TokenResponse
    {
        AccessToken = "not-a-real-token", ExpiresInSeconds = 3600, IssuedUtc = DateTime.UtcNow
    }), IConfigurableHttpClientInitializer
{
    void IConfigurableHttpClientInitializer.Initialize(ConfigurableHttpClient client)
    {
        base.Initialize(client);
        client.MessageHandler.InnerHandler = new OfflineHandler(google);
    }
}

internal sealed class OfflineHandler(OfflineGoogle google) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => google.SendAsync(request, cancellationToken);
}

internal sealed class OfflineGoogle
{
    private int _requests;
    private int _nextError;
    public int Requests => _requests;
    public HttpStatusCode NextError { set => _nextError = (int)value; }
    public bool InvalidLinkOnce { get; set; }
    public Task? InsertGate { get; set; }
    public ConcurrentDictionary<string, JsonObject> Events { get; } = new();

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        if (request.RequestUri?.Host != "www.googleapis.com" ||
            !request.RequestUri.AbsolutePath.StartsWith("/calendar/v3/calendars/primary/events", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected HTTP request: " + request.RequestUri);
        var error = Interlocked.Exchange(ref _nextError, 0);
        if (error != 0) return Error((HttpStatusCode)error);
        if (request.Method == HttpMethod.Post)
        {
            if (InsertGate != null) await InsertGate.WaitAsync(cancellationToken);
            var stream = await request.Content!.ReadAsStreamAsync(cancellationToken);
            await using var payload = request.Content.Headers.ContentEncoding.Contains("gzip")
                ? new GZipStream(stream, CompressionMode.Decompress) : stream;
            var body = (await JsonNode.ParseAsync(payload, cancellationToken: cancellationToken))!.AsObject();
            var id = body["id"]!.GetValue<string>();
            body["htmlLink"] = "https://calendar.google.com/calendar/event?eid=" + id;
            body["status"] = "confirmed";
            if (!Events.TryAdd(id, body)) return Error(HttpStatusCode.Conflict);
            var response = body.DeepClone();
            if (InvalidLinkOnce)
            {
                InvalidLinkOnce = false;
                response["htmlLink"] = "file:///C:/Windows/System32/calc.exe";
            }
            return Json(response.ToJsonString());
        }
        if (request.Method == HttpMethod.Get && Events.TryGetValue(request.RequestUri.Segments[^1], out var existing))
            return Json(existing.ToJsonString());
        return Error(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode code)
        => Json(new JsonObject { ["error"] = new JsonObject { ["code"] = (int)code, ["message"] = "Calendar test error" } }.ToJsonString(), code);
}

internal sealed class TestTicketService(Func<int, Task<List<Ticket>>>? issue = null) : ITicketService
{
    public Task<List<Ticket>> IssueTicketsAsync(int orderId) => issue?.Invoke(orderId)
        ?? throw new InvalidOperationException("Existing tickets must not be issued twice.");
    public string GenerateTicketCode(DateTime concertDate) => throw new NotSupportedException();
}

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConcertTicket.Models;
using ConcertTicket.Options;
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
using Microsoft.Extensions.Options;
using QRCoder;

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
            Console.WriteLine($"PASS: {_passed} tests; Google/Cloudinary HTTP mocked; no application data changed.");
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
                return await db.Orders.AsNoTracking().Include(o => o.Zone).Include(o => o.Tickets).SingleAsync(o => o.Id == id);
            }

            TicketService TicketService(OfflineCloudinary cloud, CloudinaryOptions? settings = null) => new(
                Options.Create(settings ?? new CloudinaryOptions { CloudName = "ticket-tests", ApiKey = "test-key", ApiSecret = "test-secret" }),
                factory, NullLogger<TicketService>.Instance, session, new TestHttpClientFactory(cloud));

            await Test("ticket issuance rejects invalid IDs, missing sessions, foreign and unpaid orders", async () =>
            {
                var cloud = new OfflineCloudinary();
                var tickets = TicketService(cloud);
                await Throws<ArgumentOutOfRangeException>(() => tickets.IssueTicketsAsync(0));
                await Throws<InvalidOperationException>(() => tickets.IssueTicketsAsync(int.MaxValue));
                var foreign = await NewOrder(tickets: 0, userId: otherUserId);
                await Throws<InvalidOperationException>(() => tickets.IssueTicketsAsync(foreign.Id));
                foreach (var status in new[] { "Pending", "Failed" })
                {
                    var unpaid = await NewOrder(status, tickets: 0);
                    await Throws<InvalidOperationException>(() => tickets.IssueTicketsAsync(unpaid.Id));
                    Assert((await ReadOrder(unpaid.Id)).Tickets.Count == 0, "unpaid order has no issued tickets");
                }
                var user = session.CurrentUser;
                session.CurrentUser = null;
                try { await Throws<InvalidOperationException>(() => tickets.IssueTicketsAsync(foreign.Id)); }
                finally { session.CurrentUser = user; }
                Assert(cloud.Requests == 0 && (await ReadOrder(foreign.Id)).Tickets.Count == 0, "no unauthorized upload or ticket");
            });

            await Test("paid orders get exact ticket quantity, Valid status, correct gate and code-only QR", async () =>
            {
                var order = await NewOrder(tickets: 0);
                var cloud = new OfflineCloudinary();
                var tickets = await TicketService(cloud).IssueTicketsAsync(order.Id);
                var saved = await ReadOrder(order.Id);
                Assert(tickets.Count == 2 && saved.Tickets.Count == 2 && cloud.Uploads.Count == 2, "quantity persisted and uploaded");
                Assert(saved.Status == "Paid" && saved.PaidAt == order.PaidAt, "payment unchanged");
                foreach (var ticket in tickets)
                {
                    Assert(Regex.IsMatch(ticket.TicketCode, "^CON-20301220-[A-Z0-9]{6}$"), "dated ticket code format");
                    Assert(ticket.Status == "Valid" && ticket.Gate == "Gate A" && ticket.QrImageUrl!.StartsWith("https://res.cloudinary.com/ticket-tests/"), "ticket details");
                    using var qrData = QRCodeGenerator.GenerateQrCode(ticket.TicketCode, QRCodeGenerator.ECCLevel.Q);
                    using var qr = new PngByteQRCode(qrData);
                    Assert(cloud.Uploads[ticket.TicketCode].SequenceEqual(qr.GetGraphic(8)), "QR payload is exactly TicketCode, with no URL or JSON");
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, ticket.TicketCode + ".png"), cloud.Uploads[ticket.TicketCode]);
                }
                int requests = cloud.Requests;
                var again = await TicketService(cloud, new CloudinaryOptions()).IssueTicketsAsync(order.Id);
                Assert(again.Select(t => t.Id).SequenceEqual(tickets.Select(t => t.Id)) && cloud.Requests == requests, "repeat returns existing tickets without keys or uploads");
            });

            await Test("ticket codes use the concert date and remain unique", async () =>
            {
                var tickets = TicketService(new OfflineCloudinary());
                var codes = Enumerable.Range(0, 40).Select(_ => tickets.GenerateTicketCode(new DateTime(2031, 1, 2))).ToList();
                Assert(codes.Distinct().Count() == codes.Count && codes.All(c => Regex.IsMatch(c, "^CON-20310102-[A-Z0-9]{6}$")), "unique, date-specific random codes");
                await Throws<ArgumentException>(() => Task.FromResult(tickets.GenerateTicketCode(default)));
            });

            await Test("partial ticket sets preserve existing codes and Used status", async () =>
            {
                var order = await NewOrder(tickets: 1);
                var original = order.Tickets.Single();
                await using (var db = factory.CreateDbContext())
                {
                    var ticket = await db.Tickets.FindAsync(original.Id);
                    ticket!.Status = "Used";
                    await db.SaveChangesAsync();
                }
                var cloud = new OfflineCloudinary();
                var issued = await TicketService(cloud).IssueTicketsAsync(order.Id);
                Assert(issued.Count == 2 && cloud.Uploads.Count == 1, "only missing ticket issued");
                var existing = issued.Single(t => t.Id == original.Id);
                Assert(existing.TicketCode == original.TicketCode && existing.QrImageUrl == original.QrImageUrl && existing.Status == "Used", "existing ticket unchanged");
            });

            await Test("missing Cloudinary keys preserve ticket codes for retry", async () =>
            {
                var order = await NewOrder(tickets: 0);
                var cloud = new OfflineCloudinary();
                await Throws<InvalidOperationException>(() => TicketService(cloud, new CloudinaryOptions()).IssueTicketsAsync(order.Id));
                var incomplete = await ReadOrder(order.Id);
                Assert(incomplete.Tickets.Count == 2 && incomplete.Tickets.All(t => t.QrImageUrl == null) && cloud.Requests == 0, "codes saved without uploading");
                var repaired = await TicketService(cloud).IssueTicketsAsync(order.Id);
                Assert(repaired.Select(t => t.TicketCode).SequenceEqual(incomplete.Tickets.OrderBy(t => t.Id).Select(t => t.TicketCode)), "retry reuses codes");
            });

            await Test("partial upload failure retries only missing QR images", async () =>
            {
                var order = await NewOrder(tickets: 0);
                var cloud = new OfflineCloudinary { FailOnRequest = 2 };
                var tickets = TicketService(cloud);
                await Throws<InvalidOperationException>(() => tickets.IssueTicketsAsync(order.Id));
                var incomplete = await ReadOrder(order.Id);
                Assert(incomplete.Tickets.Count == 2 && incomplete.Tickets.Count(t => t.QrImageUrl != null) == 1, "first image saved, all codes retained");
                var repaired = await tickets.IssueTicketsAsync(order.Id);
                Assert(cloud.Requests == 3 && cloud.Uploads.Count == 2 && repaired.All(t => t.QrImageUrl != null), "only failed image retried");
            });

            await Test("database failure after upload reuses the same Cloudinary public ID", async () =>
            {
                var order = await NewOrder(tickets: 0);
                var cloud = new OfflineCloudinary { AfterUpload = () => factory.FailNextSave = true };
                var tickets = TicketService(cloud);
                await Throws<InvalidOperationException>(() => tickets.IssueTicketsAsync(order.Id));
                var incomplete = await ReadOrder(order.Id);
                Assert(incomplete.Tickets.Count == 2 && cloud.Uploads.Count == 1, "allocated codes survive failed URL persistence");
                var repaired = await tickets.IssueTicketsAsync(order.Id);
                Assert(cloud.Uploads.Count == 2 && repaired.Count == 2 && repaired.All(t => t.QrImageUrl != null), "retry creates no duplicate image or ticket");
            });

            await Test("concurrent service instances issue one ticket set", async () =>
            {
                var order = await NewOrder(tickets: 0);
                var cloud = new OfflineCloudinary();
                var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => TicketService(cloud).IssueTicketsAsync(order.Id)));
                var saved = await ReadOrder(order.Id);
                Assert(saved.Tickets.Count == 2 && cloud.Uploads.Count == 2 && results.All(r => r.Count == 2), "one set across four callers");
                Assert(results.SelectMany(r => r.Select(t => t.TicketCode)).Distinct().Count() == 2, "same codes across callers");
            });

            await Test("unsafe Cloudinary image URLs are rejected and recoverable", async () =>
            {
                var order = await NewOrder(tickets: 0);
                var cloud = new OfflineCloudinary { InvalidUrlOnce = true };
                var tickets = TicketService(cloud);
                await Throws<InvalidOperationException>(() => tickets.IssueTicketsAsync(order.Id));
                Assert((await ReadOrder(order.Id)).Tickets.All(t => t.QrImageUrl == null), "unsafe URL never persisted");
                Assert((await tickets.IssueTicketsAsync(order.Id)).All(t => t.QrImageUrl!.StartsWith("https://res.cloudinary.com/")), "retry repairs images");
            });

            //===== Check-in (Huy) =====
            async Task<string> NewCheckInCode()
            {
                var order = await NewOrder(tickets: 0);
                return (await TicketService(new OfflineCloudinary()).IssueTicketsAsync(order.Id))[0].TicketCode;
            }

            await Test("check-in: first scan OK, second scan Used, unknown code Invalid", async () =>
            {
                var tickets = TicketService(new OfflineCloudinary());
                string code = await NewCheckInCode();
                var first = await tickets.CheckInAsync("  " + code.ToLowerInvariant() + " ");
                Assert(first.Result == "OK" && first.Ticket!.Status == "Used" && first.Ticket.Order.Zone.Concert.Name == "Đêm nhạc Calendar", "first scan admits with ticket details");
                var second = await tickets.CheckInAsync(code);
                Assert(second.Result == "Used" && second.Ticket!.TicketCode == code, "second scan reports already checked in");
                foreach (var bad in new[] { "CON-20301220-ZZZZZZ", "", "   ", "https://evil.example/qr", new string('A', 60) })
                {
                    var invalid = await tickets.CheckInAsync(bad);
                    Assert(invalid.Result == "Invalid" && invalid.Ticket == null, "unknown code is invalid: " + bad);
                }
            });

            await Test("check-in: concurrent scans of one ticket admit exactly once", async () =>
            {
                string code = await NewCheckInCode();
                var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => TicketService(new OfflineCloudinary()).CheckInAsync(code)));
                Assert(results.Count(r => r.Result == "OK") == 1 && results.Count(r => r.Result == "Used") == 5, "only one gate admits the ticket");
            });

            await Test("check-in: QR image is decoded back to the ticket code", async () =>
            {
                var tickets = TicketService(new OfflineCloudinary());
                string code = await NewCheckInCode();
                string qrPath = System.IO.Path.Combine(AppContext.BaseDirectory, "checkin-" + code + ".png");
                using (var data = QRCodeGenerator.GenerateQrCode(code, QRCodeGenerator.ECCLevel.Q))
                using (var qr = new PngByteQRCode(data))
                    System.IO.File.WriteAllBytes(qrPath, qr.GetGraphic(8));
                Assert(tickets.ReadQrCode(qrPath) == code, "QR decoded to TicketCode");

                string blankPath = System.IO.Path.Combine(AppContext.BaseDirectory, "checkin-blank.png");
                var blank = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(blank));
                using (var file = System.IO.File.Create(blankPath)) encoder.Save(file);
                Assert(tickets.ReadQrCode(blankPath) == null, "image without QR returns null");
            });

            await Test("check-in window shows green, yellow and red results", async () =>
            {
                string code = await NewCheckInCode();
                var window = new CheckInWindow(TicketService(new OfflineCloudinary()));
                try
                {
                    var box = Control<TextBox>(window, "CodeTextBox");
                    var button = Control<Button>(window, "CheckButton");
                    var result = Control<TextBlock>(window, "ResultTextBlock");
                    async Task Scan(string text)
                    {
                        box.Text = text;
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Until(() => button.IsEnabled && box.Text == "");
                    }
                    await Scan(code);
                    Assert(result.Text.Contains("THÀNH CÔNG") && Control<TextBlock>(window, "DetailTextBlock").Text.Contains("Gate A"), "green result with gate");
                    Render(window, "checkin-ok.png");
                    await Scan(code);
                    Assert(result.Text.Contains("ĐÃ CHECK-IN"), "yellow result on rescan");
                    Render(window, "checkin-used.png");
                    await Scan("CON-00000000-XXXXXX");
                    Assert(result.Text.Contains("KHÔNG HỢP LỆ"), "red result for unknown code");
                    Render(window, "checkin-invalid.png");
                }
                finally { window.Close(); }
            });

            await Test("concert search combines trimmed name, venue and calendar day, then clears filters", async () =>
            {
                await using (var db = factory.CreateDbContext())
                {
                    foreach (var (name, venue, start) in new[]
                    {
                        ("Rock Search A", "Nhà hát A", new DateTime(2030, 12, 20, 23, 0, 0)),
                        ("Rock Search B", "Sân B", new DateTime(2030, 12, 21, 0, 0, 0)),
                        ("Acoustic Search", "Nhà hát A", new DateTime(2030, 12, 20, 10, 0, 0))
                    }) db.Concerts.Add(new Concert { Name = name, Venue = venue, StartTime = start, EndTime = start.AddHours(1), Artist = "Test", Address = "Test" });
                    await db.SaveChangesAsync();
                }
                var navigation = new TestNavigation();
                var window = new MainWindow(factory, session, navigation, navigation);
                try
                {
                    window.Show();
                    var search = Control<Button>(window, "SearchButton");
                    var grid = Control<DataGrid>(window, "ConcertDataGrid");
                    await Until(() => search.IsEnabled && grid.ItemsSource != null);
                    Assert(grid.Items.Count == 4, "unfiltered concerts loaded");
                    var name = Control<TextBox>(window, "SearchTextBox");
                    var venue = Control<ComboBox>(window, "VenueComboBox");
                    var day = Control<DatePicker>(window, "ConcertDatePicker");
                    async Task Search() { search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => search.IsEnabled); }
                    name.Text = "  Rock  ";
                    await Search();
                    Assert(grid.Items.Count == 2, "trimmed keyword");
                    venue.SelectedItem = "Nhà hát A";
                    day.SelectedDate = new DateTime(2030, 12, 20);
                    await Search();
                    Assert(grid.Items.Count == 1 && ((Concert)grid.Items[0]).Name == "Rock Search A", "all filters AND together; evening concert included");
                    Render(window, "concert-search.png");
                    name.Text = "unmatched concert";
                    await Search();
                    Assert(grid.Items.Count == 0 && Control<TextBlock>(window, "ConcertStatusTextBlock").Text.Contains("Không tìm thấy"), "empty result guidance");
                    name.Clear(); venue.SelectedIndex = 0; day.SelectedDate = null;
                    await Search();
                    Assert(grid.Items.Count == 4, "clearing filters restores list");
                }
                finally { window.Close(); }
            });

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

            await Test("ticket window retries issuance before adding Calendar", async () =>
            {
                var order = await NewOrder(tickets: 0);
                int attempts = 0;
                var tickets = new TestTicketService(async id =>
                {
                    if (++attempts == 1) throw new InvalidOperationException("Cloudinary unavailable");
                    await using var db = factory.CreateDbContext();
                    var saved = await db.Orders.Include(o => o.Zone).Include(o => o.Tickets).SingleAsync(o => o.Id == id);
                    saved.Tickets.Add(NewTicket()); saved.Tickets.Add(NewTicket());
                    await db.SaveChangesAsync();
                    return saved.Tickets.ToList();
                });
                var calls = google.Requests;
                var window = new TicketWindow(tickets, service, factory, session, order.Id);
                try
                {
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    var retry = Control<Button>(window, "RetryTicketsButton");
                    await Until(() => retry.IsEnabled);
                    Assert(retry.Visibility == Visibility.Visible && google.Requests == calls, "issuance failure has retry without Calendar call");
                    retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => Control<Button>(window, "OpenCalendarButton").IsEnabled);
                    Assert(attempts == 2 && Control<ListBox>(window, "TicketListBox").Items.Count == 2, "retry displays issued tickets");
                    Render(window, "ticket-qr.png");
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
        using var data = QRCodeGenerator.GenerateQrCode(code, QRCodeGenerator.ECCLevel.Q);
        using var qr = new PngByteQRCode(data);
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, code + ".png");
        System.IO.File.WriteAllBytes(path, qr.GetGraphic(8));
        return new Ticket { TicketCode = code, Gate = "Gate A", Status = "Valid", QrImageUrl = new Uri(path).AbsoluteUri };
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
        int width = 688, height = 488;
        if (!window.IsLoaded)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
        }
        root.UpdateLayout();
        if (window.IsLoaded)
        {
            width = (int)Math.Ceiling(root.ActualWidth);
            height = (int)Math.Ceiling(root.ActualHeight);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
        {
            drawing.DrawRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        bitmap.Render(background);
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
    public Task<CheckInResult> CheckInAsync(string ticketCode) => throw new NotSupportedException();
    public string? ReadQrCode(string imagePath) => throw new NotSupportedException();
}

internal sealed class TestHttpClientFactory(HttpMessageHandler handler) : System.Net.Http.IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class OfflineCloudinary : HttpMessageHandler
{
    private int _requests;
    public int Requests => _requests;
    public int FailOnRequest { get; set; }
    public bool InvalidUrlOnce { get; set; }
    public Action? AfterUpload { get; set; }
    public ConcurrentDictionary<string, byte[]> Uploads { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int number = Interlocked.Increment(ref _requests);
        if (request.RequestUri?.Host != "api.cloudinary.com" || request.Method != HttpMethod.Post)
            throw new InvalidOperationException("Unexpected Cloudinary request");
        if (number == FailOnRequest)
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":{\"message\":\"Simulated upload failure\"}}", Encoding.UTF8, "application/json") };
        var fields = new Dictionary<string, string>();
        byte[]? png = null;
        foreach (var part in (MultipartContent)request.Content!)
        {
            string name = part.Headers.ContentDisposition!.Name!.Trim('"');
            if (name == "file") png = await part.ReadAsByteArrayAsync(cancellationToken);
            else fields[name] = await part.ReadAsStringAsync(cancellationToken);
        }
        if (png == null || !png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || fields["overwrite"] != "false")
            throw new InvalidOperationException("Expected idempotent PNG upload");
        string code = fields["public_id"];
        Uploads.TryAdd(code, png);
        string url = "https://res.cloudinary.com/ticket-tests/image/upload/v1/" + fields["folder"] + "/" + code + ".png";
        if (InvalidUrlOnce) { InvalidUrlOnce = false; url = "file:///C:/unsafe.png"; }
        var after = AfterUpload;
        AfterUpload = null;
        after?.Invoke();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["secure_url"] = url, ["public_id"] = fields["folder"] + "/" + code,
                ["resource_type"] = "image", ["format"] = "png"
            }.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }
}

internal sealed class TestNavigation : INavigationService, IAuthService
{
    public void ShowLogin() => throw new NotSupportedException();
    public void ShowMain() => throw new NotSupportedException();
    public void ShowConcertDetail(int concertId) => throw new NotSupportedException();
    public bool ShowPayment(int orderId) => throw new NotSupportedException();
    public void ShowTicket(int orderId) => throw new NotSupportedException();
    public void ShowCheckIn() => throw new NotSupportedException();
    public Task<User?> LoginWithGoogleAsync() => throw new NotSupportedException();
    public Task LogoutAsync() => throw new NotSupportedException();
}

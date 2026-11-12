using System.Net;
using System.Text.RegularExpressions;
using ConcertTicket.Options;
using ConcertTicket.Services.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConcertTicket.Services;

public class VnPayIpnListener : IHostedService
{
    private readonly IOptions<VnPayIpnOptions> _options;
    private readonly IPaymentService _payment;
    private readonly ILogger<VnPayIpnListener> _logger;
    private WebApplication? _application;

    public bool IsRunning => _application != null;

    public VnPayIpnListener(IOptions<VnPayIpnOptions> options, IPaymentService payment, ILogger<VnPayIpnListener> logger)
    {
        _options = options;
        _payment = payment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VnPayIpnOptions options = _options.Value;
        if (!options.Enabled || _application != null)
        {
            return;
        }

        if (options.Port < 1 || options.Port > 65535 || string.IsNullOrEmpty(options.Route)
            || !Regex.IsMatch(options.Route, @"^/[A-Za-z0-9/_-]+$", RegexOptions.CultureInvariant)
            || options.Route == "/health")
        {
            throw new InvalidOperationException("Cổng hoặc route VNPAY IPN không hợp lệ.");
        }

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(VnPayIpnListener).Assembly.GetName().Name,
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.WebHost.UseKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, options.Port);
            server.AddServerHeader = false;
            server.Limits.MaxRequestLineSize = 18_432;
            server.Limits.MaxRequestBodySize = 0;
        });

        WebApplication application = builder.Build();
        application.Run(context => HandleRequestAsync(context, options.Route, application.Lifetime.ApplicationStopping));

        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            _application = application;
            _logger.LogInformation("VNPAY IPN listener started on loopback port {Port}.", options.Port);
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        WebApplication? application = _application;
        _application = null;
        if (application == null)
        {
            return;
        }

        try
        {
            await application.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await application.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleRequestAsync(HttpContext context, string ipnRoute, CancellationToken stoppingToken)
    {
        if (context.Request.Path.Value == "/health")
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            await context.Response.WriteAsJsonAsync(new { status = "ok", service = "vnpay-ipn" }, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(context.Request.Path.Value, ipnRoute, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        VnPayIpnResponse response;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                response = new VnPayIpnResponse { Message = "Invalid request method" };
            }
            else
            {
                response = await _payment.ProcessIpnAsync(context.Request.QueryString.Value ?? "", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError("VNPAY IPN request failed with {ErrorType}.", exception.GetType().Name);
            response = new VnPayIpnResponse { Message = "Unable to confirm transaction" };
        }

        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(response, context.RequestAborted).ConfigureAwait(false);
    }
}

using TomYum.Gateway;

// TomYum.Gateway (docs/gateway.md): the edge for raw TCP. pre-2014 osu! builds' bancho
// (server.<domain>) and IRC are taken here and passed to the main server over HTTP(S), so the main
// server only needs HTTPS, and this can run on another box that's the only one exposed for them.
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "TOMYUM_");

var options = new GatewayOptions();
builder.Configuration.GetSection(GatewayOptions.SectionName).Bind(options);
if (string.IsNullOrWhiteSpace(options.Domain))
    throw new InvalidOperationException("Gateway:Domain (TOMYUM_GATEWAY__DOMAIN) must be the main server's domain.");
if (string.IsNullOrWhiteSpace(options.Key))
    throw new InvalidOperationException("Gateway:Key (TOMYUM_GATEWAY__KEY) must be set, the same as the main server's.");

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<Upstream>();
builder.Services.AddSingleton(new ConnectionLimiter(options.MaxConnectionsPerIp));
if (options.Bancho.Enabled)
    builder.Services.AddHostedService<BanchoGateway>();
if (options.Irc.Enabled)
    builder.Services.AddHostedService<IrcGateway>();

var app = builder.Build();
app.Logger.LogInformation("TomYum.Gateway for {Domain}, main server at {Upstream}; bancho (TCP) {Bancho}, IRC {Irc}.",
    options.Domain, options.UpstreamUri, options.Bancho.Enabled ? string.Join(", ", options.Bancho.Ports) : "off", options.Irc.Enabled ? options.Irc.Port : "off");

// For the status page and the container's health check.
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TomYum.Gateway" }));

app.Run();

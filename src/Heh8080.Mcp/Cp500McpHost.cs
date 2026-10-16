using Heh8080.Cp500Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Heh8080.Mcp;

/// <summary>
/// Stdio MCP server bound to an existing session. Does not construct a machine and does not dispose the session.
/// Logging matches the CP/M host: stderr, warning and above. Registers <see cref="Cp500Tools"/> only.
/// </summary>
public static class Cp500McpHost
{
    public static async Task RunAsync(Cp500Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(session);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<Cp500Tools>();

        await builder.Build().RunAsync(cancellationToken);
    }
}

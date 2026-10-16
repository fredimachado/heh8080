using Heh8080.Cp500Host;

namespace Heh8080.Mcp;

public static class Cp500McpHost
{
    public static Task RunAsync(Cp500Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        throw new NotImplementedException();
    }
}

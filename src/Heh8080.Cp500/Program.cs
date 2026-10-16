using System.Diagnostics;
using System.Text;
using Heh8080.Cp500Host;
using Heh8080.Mcp;

namespace Heh8080.Cp500;

/// <summary>
/// Process edge. Dump bytes go to the raw stdout stream. Refusals go to stderr. Dispose flushes after the dump.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Dispatch(Cp500Command.Parse(args), Console.OpenStandardOutput());
        }
        catch (Cp500Rejected ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Cp500Exit.Rejected;
        }
    }

    private static int Dispatch(Cp500Command command, Stream stdout)
    {
        switch (command)
        {
            case Cp500Command.Batch batch:
            {
                using Cp500Session session = Cp500Files.Load(batch.Files);
                Cp500BatchResult result = batch.Run(session);
                if (batch.Dump)
                    stdout.Write(Encoding.UTF8.GetBytes(result.Screen));
                return Cp500Exit.For(result);
            }
            case Cp500Command.Serve serve:
            {
                using Cp500Session session = Cp500Files.Load(serve.Files);
                Cp500McpHost.RunAsync(session).GetAwaiter().GetResult();
                return Cp500Exit.Success;
            }
            default:
                throw new UnreachableException(command.GetType().FullName);
        }
    }
}

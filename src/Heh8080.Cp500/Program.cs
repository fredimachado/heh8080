using System.Diagnostics;
using System.Text;
using Heh8080.Cp500Host;
using Heh8080.Devices;
using Heh8080.Mcp;
using Heh8080.Rs232;

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return Cp500Exit.Rejected;
        }
    }

    private static int Dispatch(Cp500Command command, Stream stdout)
    {
        (Rs232Link link, Cp500Files files) = command switch
        {
            Cp500Command.Batch batch => (batch.Rs232, batch.Files),
            Cp500Command.Serve serve => (serve.Rs232, serve.Files),
            _ => throw new UnreachableException(command.GetType().FullName),
        };
        Rs232Attachment cable = Rs232Endpoint.Open(link, Cp500MachineProfile.Original.ClockHz);

        Cp500Session session;
        try
        {
            session = Cp500Files.Load(files, cable);
        }
        catch
        {
            cable.Endpoint?.Dispose();
            throw;
        }

        using (session)
        {
            switch (command)
            {
                case Cp500Command.Batch batch:
                {
                    Cp500BatchResult result = batch.Run(session);
                    if (batch.Dump)
                        stdout.Write(Encoding.UTF8.GetBytes(result.Screen));
                    return Cp500Exit.For(result);
                }
                case Cp500Command.Serve:
                    Cp500McpHost.RunAsync(session).GetAwaiter().GetResult();
                    return Cp500Exit.Success;
                default:
                    throw new UnreachableException(command.GetType().FullName);
            }
        }
    }
}

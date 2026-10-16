namespace Heh8080.Cp500Host;

/// <summary>Refusal before a result exists: bad argv, bad files, unmapped keys, bad memory range, flush failure.</summary>
public sealed class Cp500Rejected : Exception
{
    public Cp500Rejected(string message) : base(message) { }
}

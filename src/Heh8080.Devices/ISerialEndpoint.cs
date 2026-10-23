namespace Heh8080.Devices;

/// <summary>Modem-status pins the UART presents on E8h. One means the signal is true.</summary>
/// <summary>DTR and RTS from the UART control word (EAh while E8h bit 1 is set).</summary>
public readonly record struct ModemOutputs(bool Dtr, bool Rts);

public readonly record struct ModemInputs(bool Cts, bool Dsr, bool Carrier, bool Ring, bool ReceiveMark)
{
    /// <summary>No cable. The receiver idles at mark.</summary>
    public static ModemInputs Idle { get; } = new(false, false, false, false, true);

    /// <summary>A connected raw cable. Ring stays clear.</summary>
    public static ModemInputs Online { get; } = new(true, true, true, false, true);
}

/// <summary>
/// Byte pipe behind the UART. Implementations must not be opened from <c>Heh8080.Devices</c>.
/// <see cref="AdvanceCycles"/> counts emulated time and may run on the CPU thread.
/// </summary>
public interface ISerialEndpoint : IDisposable
{
    /// <summary>Dequeues one received byte, if the endpoint has one waiting.</summary>
    bool TryRead(out byte value);

    /// <summary>Accepts one transmitted byte.</summary>
    void Write(byte value);

    /// <summary>Live modem pins, including the receiver line while the UART is idle.</summary>
    ModemInputs Inputs { get; }

    /// <summary>Counts emulated CPU cycles. A raw cable does nothing here.</summary>
    void AdvanceCycles(long cycles);

    /// <summary>Reflects modem control outputs from the UART. Endpoints that ignore DTR/RTS use the default.</summary>
    void SetModemOutputs(ModemOutputs outputs) { }
}

/// <summary>
/// Pairs the profile flag with the cable. <see cref="Apply"/> is the only production writer of
/// <see cref="Cp500MachineProfile.Rs232Installed"/>.
/// </summary>
public readonly record struct Rs232Attachment(bool Installed, ISerialEndpoint? Endpoint)
{
    /// <summary>Board absent. The constructor registers no UART.</summary>
    public static Rs232Attachment None { get; } = new(false, null);

    /// <summary>Returns a profile whose RS-232 flag matches <see cref="Installed"/>.</summary>
    public Cp500MachineProfile Apply(Cp500MachineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile with { Rs232Installed = Installed };
    }
}

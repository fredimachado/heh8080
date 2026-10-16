using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// TRS-80 Model III RS-232 board. E8h, E9h, EAh, and EBh are four registers on one device.
/// The bit clock is 16 samples per bit. <see cref="Cp500Machine.Step"/> is the production caller of
/// <see cref="AdvanceCycles"/>.
/// </summary>
public sealed class Cp500Rs232Device : IIoDevice
{
    /// <summary>Modem status.</summary>
    public const byte ModemPort = 0xE8;

    /// <summary>Baud-rate generator. The read is FFh.</summary>
    public const byte BaudPort = 0xE9;

    /// <summary>UART control and status.</summary>
    public const byte ControlPort = 0xEA;

    /// <summary>UART data.</summary>
    public const byte DataPort = 0xEB;

    /// <summary>Reset control word: 8 bits, parity inhibited, one stop bit, break off, RTS clear, DTR clear.</summary>
    public const byte ResetControl = 0x6C;

    // Baud times 1000, so 134.5 baud is exact. One 16x sample when accum covers clockHz milliseconds.
    private static readonly int[] Millibaud =
    [
        50_000, 75_000, 110_000, 134_500, 150_000, 300_000, 600_000, 1_200_000,
        1_800_000, 2_000_000, 2_400_000, 3_600_000, 4_800_000, 7_200_000,
        9_600_000, 19_200_000,
    ];

    private readonly long _clockHz;
    private readonly ISerialEndpoint? _endpoint;

    private bool _controlLoad;
    private byte _control = ResetControl;
    private int _txBaud;
    private int _rxBaud;
    private long _txAccum;
    private long _rxAccum;

    private byte _holding;
    private bool _holdingFull;
    private bool _tbmt = true;

    private byte _data;
    private bool _dav;
    private bool _overrun;
    private bool _framing;
    private bool _parity;

    private bool _txActive;
    private int _txBit;
    private int _txSample;
    private int _txBitCount;
    private byte _txData;

    private bool _rxActive;
    private int _rxBit;
    private int _rxSample;
    private int _rxBitCount;
    private int _rxStopIndex;
    private ulong _rxPattern;
    private byte _rxData;
    private bool _rxStopLow;
    private bool _rxLineHigh = true;

    /// <summary>Creates the board. A null endpoint is an installed board with no cable.</summary>
    public Cp500Rs232Device(long clockHz, ISerialEndpoint? endpoint)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockHz);
        _clockHz = clockHz;
        _endpoint = endpoint;
        Reset();
    }

    /// <summary>Raised when transmit-empty, data-received, or a receiver error changes.</summary>
    internal Action? StatusChanged { get; set; }

    internal bool TransmitHoldingEmpty => _tbmt;

    internal bool DataReceived => _dav;

    internal bool ReceiverError => _overrun || _framing || _parity;

    /// <summary>Claims E8h-EBh for input and output.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, ModemPort, DataPort);
    }

    /// <summary>
    /// Forwards cycles to the endpoint, then runs the transmit and receive 16x clocks.
    /// </summary>
    public void AdvanceCycles(long cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycles);
        _endpoint?.AdvanceCycles(cycles);

        bool tbmt = _tbmt;
        bool dav = _dav;
        bool error = ReceiverError;
        Clock(ref _txAccum, _txBaud, cycles, OnTransmitSample);
        Clock(ref _rxAccum, _rxBaud, cycles, OnReceiveSample);
        if (tbmt != _tbmt || dav != _dav || error != ReceiverError)
            StatusChanged?.Invoke();
    }

    /// <summary>Restores the power-on UART: control load off, 50 baud, holding empty, errors clear.</summary>
    public void Reset()
    {
        _controlLoad = false;
        _control = ResetControl;
        _txBaud = 0;
        _rxBaud = 0;
        _txAccum = 0;
        _rxAccum = 0;
        _holding = 0;
        _holdingFull = false;
        _tbmt = true;
        _data = 0;
        _dav = false;
        _overrun = false;
        _framing = false;
        _parity = false;
        _txActive = false;
        _rxActive = false;
        _rxLineHigh = true;
        StatusChanged?.Invoke();
    }

    public byte In(byte port) => port switch
    {
        ModemPort => ModemStatus(),
        BaudPort => 0xFF,
        ControlPort => (byte)(StatusBits() | 0x07),
        DataPort => ReadData(),
        _ => 0xFF,
    };

    public void Out(byte port, byte value)
    {
        switch (port)
        {
            case ModemPort:
                _controlLoad = (value & 0x02) != 0;
                break;
            case BaudPort:
                _txBaud = (value >> 4) & 0x0F;
                _rxBaud = value & 0x0F;
                break;
            case ControlPort:
                if (_controlLoad)
                    _control = value;
                break;
            case DataPort:
                _holding = value;
                _holdingFull = true;
                _tbmt = false;
                StatusChanged?.Invoke();
                break;
        }
    }

    private void Clock(ref long accum, int baudIndex, long cycles, Action sample)
    {
        accum += cycles * Millibaud[baudIndex] * 16L;
        long span = _clockHz * 1000L;
        while (accum >= span)
        {
            accum -= span;
            sample();
        }
    }

    private void OnTransmitSample()
    {
        if (!_txActive)
        {
            if (!_holdingFull || BreakAsserted)
                return;

            FramePlan plan = FramePlan.From(_control);
            _txData = (byte)(_holding & ((1 << plan.DataBits) - 1));
            _holdingFull = false;
            _tbmt = true;
            _txActive = true;
            _txBit = 0;
            _txSample = 0;
            _txBitCount = plan.BitCount;
        }

        _txSample++;
        if (_txSample < 16)
            return;

        _txSample = 0;
        _txBit++;
        if (_txBit < _txBitCount)
            return;

        _endpoint?.Write(_txData);
        _txActive = false;
    }

    private void OnReceiveSample()
    {
        if (!_rxActive && !TryStartReceive())
            return;

        _rxLineHigh = ((_rxPattern >> _rxBit) & 1UL) != 0;
        // The AY-3-1015 judges the stop bit at the center of its 16 samples.
        if (_rxBit == _rxStopIndex && _rxSample == 7)
            JudgeStop();

        _rxSample++;
        if (_rxSample < 16)
            return;

        _rxSample = 0;
        _rxBit++;
        if (_rxBit >= _rxBitCount)
            _rxActive = false;
    }

    private bool TryStartReceive()
    {
        ModemInputs inputs = _endpoint?.Inputs ?? ModemInputs.Idle;
        if (!inputs.ReceiveMark)
            BeginReceive(0, breakFrame: true);
        else if (_endpoint is not null && _endpoint.TryRead(out byte value))
            BeginReceive(value, breakFrame: false);
        else
            return false;

        return true;
    }

    private void BeginReceive(byte value, bool breakFrame)
    {
        FramePlan plan = FramePlan.From(_control);
        int mask = (1 << plan.DataBits) - 1;
        _rxData = (byte)(value & mask);
        _rxStopLow = breakFrame;
        _rxPattern = 0;
        int index = 1;
        int ones = 0;
        for (int bit = 0; bit < plan.DataBits; bit++)
        {
            if ((_rxData & (1 << bit)) != 0)
            {
                _rxPattern |= 1UL << index;
                ones++;
            }

            index++;
        }

        if (plan.Parity)
        {
            bool parityBit = plan.Even ? (ones & 1) != 0 : (ones & 1) == 0;
            if (parityBit && !breakFrame)
                _rxPattern |= 1UL << index;
            index++;
        }

        if (!breakFrame)
            _rxPattern |= 1UL << index;
        if (plan.TwoStop && !breakFrame)
            _rxPattern |= 1UL << (index + 1);

        _rxBitCount = plan.BitCount;
        _rxStopIndex = plan.StopIndex;
        _rxActive = true;
        _rxBit = 0;
        _rxSample = 0;
        _rxLineHigh = false;
    }

    private void JudgeStop()
    {
        bool overrun = _dav;
        if (!_dav)
        {
            _data = _rxData;
            _dav = true;
        }

        _overrun = overrun;
        _framing = _rxStopLow;
        _parity = false;
    }

    private byte ModemStatus()
    {
        ModemInputs inputs = _endpoint?.Inputs ?? ModemInputs.Idle;
        byte status = 0;
        if (inputs.Cts)
            status |= 0x80;
        if (inputs.Dsr)
            status |= 0x40;
        if (inputs.Carrier)
            status |= 0x20;
        if (inputs.Ring)
            status |= 0x10;
        bool mark = _rxActive ? _rxLineHigh : inputs.ReceiveMark;
        if (mark)
            status |= 0x02;
        return status;
    }

    private byte StatusBits()
    {
        byte status = 0;
        if (_dav)
            status |= 0x80;
        if (_tbmt)
            status |= 0x40;
        if (_overrun)
            status |= 0x20;
        if (_framing)
            status |= 0x10;
        if (_parity)
            status |= 0x08;
        return status;
    }

    private byte ReadData()
    {
        byte value = _data;
        if (_dav)
        {
            _dav = false;
            StatusChanged?.Invoke();
        }

        return value;
    }

    private bool BreakAsserted => (_control & 0x04) == 0;

    private readonly record struct FramePlan(int DataBits, int BitCount, int StopIndex, bool Parity, bool Even, bool TwoStop)
    {
        public static FramePlan From(byte control)
        {
            int dataBits = ((control >> 5) & 0x03) switch
            {
                0 => 5,
                2 => 6,
                1 => 7,
                3 => 8,
                _ => throw new InvalidOperationException("Word length is two bits."),
            };
            bool parity = (control & 0x08) == 0;
            bool twoStop = (control & 0x10) != 0;
            int stopIndex = 1 + dataBits + (parity ? 1 : 0);
            int bitCount = stopIndex + (twoStop ? 2 : 1);
            return new(dataBits, bitCount, stopIndex, parity, (control & 0x80) != 0, twoStop);
        }
    }
}

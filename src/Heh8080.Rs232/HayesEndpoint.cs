using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Heh8080.Devices;

namespace Heh8080.Rs232;

/// <summary>
/// Hayes bridge in front of the UART. It binds a listening socket, speaks AT in command mode,
/// and moves bytes in data mode. Guard time is emulated cycles from <see cref="AdvanceCycles"/>.
/// </summary>
internal sealed class HayesEndpoint : ISerialEndpoint
{
    private const int ConnectTimeoutSeconds = 30;
    private const int RingsBeforeDrop = 10;

    private readonly object _gate = new();
    private readonly long _clockHz;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private readonly Queue<byte> _rx = new();
    private readonly StringBuilder _command = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private TcpClient? _inbound;
    private TcpClient? _dialed;
    private int _dialGeneration;
    private DialState _dialState;
    private bool _dataMode;
    private bool _echo = true;
    private int _s0;
    private bool _ringing;
    private bool _ringStarted;
    private long _phaseRemaining;
    private int _rings;
    private int _plusCount;
    private long _quiet;
    private bool _dtr;
    private bool _rts = true;
    private bool _disposed;

    /// <summary>Binds <paramref name="port"/> on all interfaces. Port 0 asks the OS for a free port.</summary>
    public HayesEndpoint(int port, long clockHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockHz);
        _clockHz = clockHz;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _accept = AcceptLoop();
    }

    internal int LocalPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ModemInputs Inputs
    {
        get
        {
            lock (_gate)
            {
                ObserveDial();
                return new ModemInputs(true, true, _stream is not null, _ringing, true);
            }
        }
    }

    public bool TryRead(out byte value)
    {
        lock (_gate)
        {
            ObserveDial();
            Drain();
            if (!_rts || _rx.Count == 0)
            {
                value = 0;
                return false;
            }

            value = _rx.Dequeue();
            return true;
        }
    }

    public void Write(byte value)
    {
        DialRequest? dial = null;
        lock (_gate)
        {
            ObserveDial();
            if (_dataMode)
            {
                TakeData(value);
                return;
            }

            if (value == (byte)'\n')
                return;

            if (_echo)
                _rx.Enqueue(value);

            if (value is 0x08 or 0x7F)
            {
                if (_command.Length > 0)
                    _command.Length--;
                return;
            }

            if (value != (byte)'\r')
            {
                _command.Append(char.ToUpperInvariant((char)value));
                return;
            }

            string line = _command.ToString();
            _command.Clear();
            dial = Execute(line);
        }

        if (dial is { } request)
        {
            // Finish the TCP dial before Write returns so CONNECT is already in
            // _rx when the guest UART finishes the CR. An async dial races the
            // guest's wait loop under turbo (emulated time << wall connect).
            DialAsync(request.Host, request.Port, request.Generation).GetAwaiter().GetResult();
            lock (_gate)
                ObserveDial();
        }
    }

    public void SetModemOutputs(ModemOutputs outputs)
    {
        lock (_gate)
        {
            bool wasDtr = _dtr;
            _dtr = outputs.Dtr;
            _rts = outputs.Rts;
            if (wasDtr && !_dtr)
                Hangup();
        }
    }

    public void AdvanceCycles(long cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycles);
        lock (_gate)
        {
            ObserveDial();
            AdvanceRing(cycles);
            if (!_dataMode)
                return;

            // The UART adds these cycles before Write, then Write clears the counter, so the
            // byte's own instruction is not guard time.
            _quiet += cycles;
            if (_plusCount == 3 && _quiet >= _clockHz)
                Escape();
        }
    }

    public void Dispose()
    {
        Task accept;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _dialGeneration++;
            CloseSockets();
            accept = _accept;
        }

        _stop.Cancel();
        _listener.Stop();
        try
        {
            accept.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    private readonly record struct DialRequest(string Host, int Port, int Generation);

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                client.NoDelay = true;
                lock (_gate)
                {
                    if (_disposed || _inbound is not null || _stream is not null)
                    {
                        client.Dispose();
                        continue;
                    }

                    _inbound = client;
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested || _disposed)
        {
        }
    }

    private DialRequest? Execute(string line)
    {
        if (line.Length < 2 || !line.StartsWith("AT", StringComparison.Ordinal))
        {
            Reply("ERROR");
            return null;
        }

        // Compound AT lines (ATE0V1, ATE0S0=1, …). Dial must be last.
        ReadOnlySpan<char> rest = line.AsSpan(2);
        while (!rest.IsEmpty)
        {
            if (rest.StartsWith("E0", StringComparison.Ordinal))
            {
                _echo = false;
                rest = rest[2..];
                continue;
            }

            if (rest.StartsWith("E1", StringComparison.Ordinal))
            {
                _echo = true;
                rest = rest[2..];
                continue;
            }

            if (rest.Length >= 2 && (rest[0] is 'V' or 'Q') && (rest[1] is '0' or '1'))
            {
                // Always verbose / results-on; accept so ATE0V1 from term/irc works.
                rest = rest[2..];
                continue;
            }

            if (rest.StartsWith("S0=", StringComparison.Ordinal))
            {
                rest = rest[3..];
                int digits = 0;
                while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
                    digits++;
                if (digits == 0 || !byte.TryParse(rest[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out byte n))
                {
                    Reply("ERROR");
                    return null;
                }

                _s0 = n;
                rest = rest[digits..];
                if (_inbound is not null && _s0 >= 1 && _rings >= _s0)
                {
                    Answer();
                    return null;
                }

                continue;
            }

            if (rest.StartsWith("Z", StringComparison.Ordinal))
            {
                ResetModem();
                rest = rest[1..];
                continue;
            }

            if (rest is "H" or "H0")
            {
                Hangup();
                Reply("OK");
                return null;
            }

            if (rest is "H1")
            {
                Reply("ERROR");
                return null;
            }

            if (rest is "O")
            {
                if (_stream is not null)
                {
                    EnterData();
                    Reply("CONNECT");
                }
                else
                {
                    Reply("NO CARRIER");
                }

                return null;
            }

            if (rest is "A")
            {
                if (_inbound is not null)
                    Answer();
                else
                    Reply("NO CARRIER");
                return null;
            }

            if (rest.StartsWith("DT", StringComparison.Ordinal) ||
                rest.StartsWith("D", StringComparison.Ordinal) && rest.Length > 1 && rest[1] is not 'T')
            {
                // ATDT, ATD, ATDP host:port
                ReadOnlySpan<char> target = rest.StartsWith("DT", StringComparison.Ordinal) || rest.StartsWith("DP", StringComparison.Ordinal)
                    ? rest[2..]
                    : rest[1..];
                return BeginDial(target.ToString());
            }

            Reply("ERROR");
            return null;
        }

        Reply("OK");
        return null;
    }

    private void ResetModem()
    {
        Hangup();
        _echo = true;
        _s0 = 0;
        _command.Clear();
        _dataMode = false;
        _plusCount = 0;
        _quiet = 0;
    }

    private void Hangup()
    {
        _dialGeneration++;
        _dialState = DialState.None;
        CloseSockets();
        _dataMode = false;
        _plusCount = 0;
        _quiet = 0;
        _ringing = false;
        _ringStarted = false;
        _rings = 0;
    }

    private DialRequest? BeginDial(string target)
    {
        if (!TryParseEndpoint(target, out string host, out int port))
        {
            Reply("ERROR");
            return null;
        }

        _dialState = DialState.Pending;
        return new DialRequest(host, port, _dialGeneration);
    }

    private static void Observe(Task task)
    {
        task.ContinueWith(
            completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DialAsync(string host, int port, int generation)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || generation != _dialGeneration)
                {
                    client.Dispose();
                    return;
                }

                _dialed = client;
                _dialState = DialState.Connected;
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            client.Dispose();
            lock (_gate)
            {
                if (_disposed || generation != _dialGeneration)
                    return;

                _dialState = DialState.Failed;
            }
        }
    }

    private void ObserveDial()
    {
        if (_dialState == DialState.Connected && _dialed is not null)
        {
            TcpClient client = _dialed;
            _dialed = null;
            _dialState = DialState.None;
            _client = client;
            _stream = client.GetStream();
            EnterData();
            Reply("CONNECT");
            return;
        }

        if (_dialState == DialState.Failed)
        {
            _dialState = DialState.None;
            Reply("NO CARRIER");
        }
    }

    private void SetS0(string text)
    {
        if (!byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out byte n))
        {
            Reply("ERROR");
            return;
        }

        _s0 = n;
        if (_inbound is not null && _s0 >= 1 && _rings >= _s0)
            Answer();
        else
            Reply("OK");
    }

    private void AdvanceRing(long cycles)
    {
        if (_inbound is null)
            return;

        if (!_ringStarted)
        {
            _ringStarted = true;
            _ringing = true;
            _phaseRemaining = 2 * _clockHz;
            CountRing();
        }

        while (cycles > 0 && _inbound is not null)
        {
            if (cycles < _phaseRemaining)
            {
                _phaseRemaining -= cycles;
                return;
            }

            cycles -= _phaseRemaining;
            if (_ringing)
            {
                _ringing = false;
                _phaseRemaining = 4 * _clockHz;
            }
            else
            {
                _ringing = true;
                _phaseRemaining = 2 * _clockHz;
                CountRing();
            }
        }
    }

    private void CountRing()
    {
        _rings++;
        if (_s0 >= 1 && _rings >= _s0)
            Answer();
        else if (_s0 == 0 && _rings >= RingsBeforeDrop)
            DropInbound();
    }

    private void Answer()
    {
        if (_inbound is null)
            return;

        _client = _inbound;
        _inbound = null;
        _stream = _client.GetStream();
        _ringing = false;
        _ringStarted = false;
        EnterData();
        Reply("CONNECT");
    }

    private void DropInbound()
    {
        _inbound?.Dispose();
        _inbound = null;
        _ringing = false;
        _ringStarted = false;
        _rings = 0;
    }

    private void EnterData()
    {
        _dataMode = true;
        _plusCount = 0;
        _quiet = 0;
    }

    private void Escape()
    {
        _plusCount = 0;
        _quiet = 0;
        _dataMode = false;
        Reply("OK");
    }

    private void TakeData(byte value)
    {
        if (value == (byte)'+')
        {
            bool gapTooLong = _plusCount > 0 && _quiet >= _clockHz;
            bool guarded = _plusCount == 0 && _quiet >= _clockHz;
            if (!gapTooLong && (guarded || (_plusCount is > 0 and < 3)))
            {
                _plusCount++;
                _quiet = 0;
                return;
            }

            ForwardHeld();
        }
        else if (_plusCount > 0)
        {
            ForwardHeld();
        }

        _quiet = 0;
        Send(value);
    }

    private void ForwardHeld()
    {
        for (int i = 0; i < _plusCount; i++)
            Send((byte)'+');
        _plusCount = 0;
    }

    private void Send(byte value)
    {
        try
        {
            _stream?.WriteByte(value);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Hangup();
        }
    }

    private void Drain()
    {
        if (_stream is null || !_dataMode || !_stream.DataAvailable)
            return;

        try
        {
            while (_stream.DataAvailable)
                _rx.Enqueue((byte)_stream.ReadByte());
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Hangup();
        }
    }

    private void CloseSockets()
    {
        _dialed?.Dispose();
        _dialed = null;
        _stream = null;
        _client?.Dispose();
        _client = null;
        _inbound?.Dispose();
        _inbound = null;
    }

    private void Reply(string code)
    {
        foreach (char c in code)
            _rx.Enqueue((byte)c);
        _rx.Enqueue((byte)'\r');
        _rx.Enqueue((byte)'\n');
    }

    private static bool TryParseEndpoint(string target, out string host, out int port)
    {
        host = "";
        port = 0;
        int colon = target.IndexOf(':');
        if (colon <= 0 || colon != target.LastIndexOf(':'))
            return false;

        host = target[..colon];
        return int.TryParse(target[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;
    }

    private enum DialState
    {
        None,
        Pending,
        Connected,
        Failed,
    }
}

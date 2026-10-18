#pragma warning disable CA1416 // Host TCP. The browser target compiles this file and does not run it.

using System.Net;
using System.Net.Sockets;
using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>WIZnet W5100 in indirect mode on D0h-D3h for CP-500 network tests.</summary>
public sealed class W5100Device : IIoDevice
{
    public const byte PortMr = 0xD0;
    public const byte PortAr0 = 0xD1;
    public const byte PortAr1 = 0xD2;
    public const byte PortDr = 0xD3;

    private const byte MrIndirect = 0x03;
    private const byte CmdOpen = 0x01;
    private const byte CmdListen = 0x02;
    private const byte CmdConnect = 0x04;
    private const byte CmdClose = 0x10;
    private const byte CmdSend = 0x20;
    private const byte CmdRecv = 0x40;

    private const byte ProtoTcp = 0x01;
    private const byte ProtoUdp = 0x02;

    private const byte SrClosed = 0x00;
    private const byte SrInit = 0x13;
    private const byte SrListen = 0x14;
    private const byte SrEstablished = 0x17;

        // Two seconds early. The milestone boot reaches date that long after settime.
    private static readonly byte[] TimePort37Payload = [0xED, 0x00, 0x37, 0x7E];

    private static readonly byte[] DnsResponse =
    [
        0x12, 0x34, 0x81, 0x80, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
        0x03, (byte)'w', (byte)'w', (byte)'w', 0x07, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
        0x03, (byte)'c', (byte)'o', (byte)'m', 0x00, 0x00, 0x01, 0x00, 0x01,
        0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x04,
        127, 0, 0, 1,
    ];

    private readonly byte[] _mem = new byte[65536];
    private readonly SocketChannel[] _channels = new SocketChannel[4];
    private byte _mr = 0xFF;
    private ushort _address;
    private ushort _lastArSelect;
    private bool _drWordLow;
    private bool _wrWordLow;

    public W5100Device()
    {
        SeedNetworkConfig();
        for (int i = 0; i < _channels.Length; i++)
            _channels[i] = new SocketChannel(i, _mem);
    }

    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, PortMr, PortDr);
    }

    public byte In(byte port)
    {
        switch (port)
        {
            case PortMr:
                return _mr;
            case PortAr0:
                return (byte)(_address >> 8);
            case PortAr1:
                return (byte)_address;
            case PortDr:
                bool readingStatus = _address is >= 0x0400 and <= 0x0700 && (_address & 0x00FF) == 0x03;
                if (readingStatus)
                    PollSocket((_address >> 8) - 4, readingStatus: true);
                if ((_address & 0x00FF) == 0x26 && _address is >= 0x0426 and <= 0x0726)
                    PollSocket((_address >> 8) - 4, readingStatus: false);
                return ReadDr();
            default:
                return 0xFF;
        }
    }

    public void Out(byte port, byte value)
    {
        switch (port)
        {
            case PortMr:
                _mr = value;
                break;
            case PortAr0:
                _address = (ushort)((_address & 0x00FF) | (value << 8));
                break;
            case PortAr1:
                _address = (ushort)((_address & 0xFF00) | value);
                _drWordLow = false;
                if (_address != _lastArSelect || !IsSocketWordReg(_address))
                    _wrWordLow = false;

                _lastArSelect = _address;
                break;
            case PortDr:
                WriteDr(value);
                break;
        }
    }

    private static bool IsSocketWordReg(ushort address) =>
        address is >= 0x0400 and <= 0x07FF && (address & 0x00FF) is 0x04 or 0x10 or 0x20 or 0x24 or 0x26 or 0x28;

    private byte ReadDr()
    {
        if (!IsSocketWordReg(_address))
        {
            byte plain = _mem[_address];
            _address++;
            return plain;
        }

        if (!_drWordLow)
        {
            _drWordLow = true;
            return _mem[_address];
        }

        _drWordLow = false;
        return _mem[_address + 1];
    }

    private void WriteDr(byte value)
    {
        if (!IsSocketWordReg(_address))
        {
            _mem[_address] = value;
            if (_address is >= 0x0401 and <= 0x0701 && (_address & 0x00FF) == 0x01)
                RunCommand((_address >> 8) - 4);
            _address++;
            return;
        }

        if (!_wrWordLow)
        {
            _wrWordLow = true;
            _mem[_address] = value;
            return;
        }

        _wrWordLow = false;
        _mem[_address + 1] = value;
    }

    private void SeedNetworkConfig()
    {
        WriteCommon(0x0001, 0x0A, 0x00, 0x00, 0x02);
        WriteCommon(0x0005, 0xFF, 0xFF, 0xFF, 0x00);
        WriteCommon(0x0009, 0x02, 0x43, 0x50, 0x35, 0x30, 0x30);
        WriteCommon(0x000F, 0x0A, 0x00, 0x02, 0x0F);
        _mem[0x001A] = 0x55;
        _mem[0x001B] = 0x55;
    }

    private void WriteCommon(int start, params byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            _mem[start + i] = bytes[i];
    }

    private void RunCommand(int socket)
    {
        ushort baseAddr = (ushort)(0x0400 + socket * 0x0100);
        byte command = _mem[baseAddr + 1];
        if (command == 0)
            return;

        var channel = _channels[socket];
        switch (command)
        {
            case CmdOpen:
                channel.Open(_mem[baseAddr]);
                _mem[baseAddr + 3] = channel.Protocol == ProtoUdp ? SrInit : SrInit;
                break;
            case CmdListen:
                channel.Listen();
                _mem[baseAddr + 3] = SrListen;
                break;
            case CmdConnect:
                channel.Connect(ReadPort(baseAddr + 0x10));
                break;
            case CmdSend:
                channel.Send(_mem);
                break;
            case CmdRecv:
                channel.Recv(_mem);
                break;
            case CmdClose:
                channel.Close();
                _mem[baseAddr + 3] = SrClosed;
                break;
        }

        _mem[baseAddr + 1] = 0;
    }

    private ushort ReadPort(int address) =>
        (ushort)(_mem[address] << 8 | _mem[address + 1]);

    private void PollSocket(int socket, bool readingStatus)
    {
        SocketChannel channel = _channels[socket];
        channel.Poll(_mem, channel.SeenEstablished);
        if (readingStatus && _mem[(0x0400 + socket * 0x0100) + 3] == SrEstablished)
            channel.SeenEstablished = true;
    }

    private sealed class SocketChannel
    {
        private const int TxBase = 0x4000;
        private const int RxBase = 0x6000;
        private const int BufferStep = 0x0800;
        private const int BufferMask = 0x07FF;

        private readonly int _index;
        private readonly byte[] _mem;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private TcpListener? _listener;
        private byte _protocol;
        private bool _time37;
        private readonly List<byte> _rxQueue = [];
        private int _txSentPtr;
        private bool _seenEstablished;

        public bool SeenEstablished
        {
            get => _seenEstablished;
            set => _seenEstablished = value;
        }

        public SocketChannel(int index, byte[] mem)
        {
            _index = index;
            _mem = mem;
        }

        public byte Protocol => _protocol;

        public void Open(byte mode)
        {
            Close();
            _protocol = (byte)(mode & 0x0F);
            _time37 = false;
            _rxQueue.Clear();
            _txSentPtr = 0;
            WritePortReg(0x20, 0x0800);
            WritePortReg(0x24, 0);
            WritePortReg(0x28, 0);
            WritePortReg(0x26, 0);
            WriteStatus(SrInit);
        }

        public void Listen()
        {
            Close();
            ushort port = ReadPortReg(4);
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            WriteStatus(SrListen);
        }

        public void Connect(ushort port)
        {
            ushort reg = BaseReg();
            if (port == 37
                && _mem[reg + 0x0C] == 10
                && _mem[reg + 0x0D] == 0
                && _mem[reg + 0x0E] == 2
                && _mem[reg + 0x0F] == 2)
            {
                Close();
                _time37 = true;
                WriteStatus(SrEstablished);
                EnqueueRx(TimePort37Payload);
                return;
            }

            Close();

            try
            {
                _client = new TcpClient();
                _client.Connect(IPAddress.Loopback, port);
                _stream = _client.GetStream();
                WriteStatus(SrEstablished);
                var deadline = DateTime.UtcNow.AddMilliseconds(50);
                do
                {
                    DrainStream(false);
                    if (_rxQueue.Count > 0 || IsPeerFinished())
                        break;
                    Thread.Sleep(1);
                }
                while (DateTime.UtcNow < deadline);
            }
            catch (SocketException)
            {
                WriteStatus(SrClosed);
            }
        }

        public void Send(byte[] mem)
        {
            if (_protocol == ProtoUdp)
            {
                if (ReadPortReg(0x10) == 53)
                    EnqueueRx(DnsResponse);
                return;
            }

            if (_time37)
                return;

            FlushTxToHost(mem);
        }

        public void Recv(byte[] mem)
        {
            int size = ReadPortReg(0x26);
            if (size == 0)
                return;

            if (_rxQueue.Count > size)
                _rxQueue.RemoveRange(0, size);
            else
                _rxQueue.Clear();

            int rd = ReadPortReg(0x28);
            rd = (rd + size) & BufferMask;
            WritePortReg(0x28, (ushort)rd);
            WritePortReg(0x26, 0);
            if (_time37 && _rxQueue.Count == 0)
                WriteStatus(SrClosed);
        }

        public void Poll(byte[] mem, bool allowClose)
        {
            if (_listener is not null)
                AcceptPending();

            if (_stream is not null)
                DrainStream(allowClose);

            UpdateRxSize();
        }

        public void Close()
        {
            _stream?.Dispose();
            _client?.Dispose();
            _listener?.Stop();
            _stream = null;
            _client = null;
            _listener = null;
            _time37 = false;
            _rxQueue.Clear();
            _txSentPtr = 0;
            _seenEstablished = false;
            WritePortReg(0x26, 0);
            WritePortReg(0x20, 0x0800);
        }

        private void AcceptPending()
        {
            if (_listener is null || !_listener.Pending())
                return;

            _client = _listener.AcceptTcpClient();
            _stream = _client.GetStream();
            _listener.Stop();
            _listener = null;
            WriteStatus(SrEstablished);
        }

        private void DrainStream(bool allowClose)
        {
            if (_stream is null)
                return;

            int avail = _client?.Available ?? 0;
            if (avail <= 0)
            {
                if (allowClose && _rxQueue.Count == 0 && IsPeerFinished())
                    WriteStatus(SrClosed);
                return;
            }

            var chunk = new byte[Math.Min(avail, 512)];
            int read = _stream.Read(chunk, 0, chunk.Length);
            if (read <= 0)
            {
                if (_rxQueue.Count == 0)
                    WriteStatus(SrClosed);
                return;
            }

            EnqueueRx(chunk.AsSpan(0, read));
            if (_client?.Available > 0)
                return;

            FlushTxToHost(_mem);
        }

        private bool IsPeerFinished()
        {
            Socket? socket = _client?.Client;
            if (socket is null)
                return false;

            try
            {
                return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
            }
            catch (SocketException)
            {
                return true;
            }
        }

        private void FlushTxToHost(byte[] mem)
        {
            if (_stream is null)
                return;

            int ptr = ReadPortReg(0x24);
            int pending = (ptr - _txSentPtr) & BufferMask;
            if (pending == 0)
                return;

            int offset = TxBase + _index * BufferStep;
            var block = new byte[pending];
            for (int i = 0; i < pending; i++)
                block[i] = mem[(offset + ((_txSentPtr + i) & BufferMask)) & 0xFFFF];

            try
            {
                _stream.Write(block, 0, block.Length);
                _stream.Flush();
            }
            catch (IOException)
            {
                WriteStatus(SrClosed);
                return;
            }

            _txSentPtr = ptr;
            WritePortReg(0x20, 0x0800);
        }

        private void EnqueueRx(ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
                _rxQueue.Add(b);
            CopyRxToChip();
        }

        private void CopyRxToChip()
        {
            if (_rxQueue.Count == 0)
                return;

            int offset = RxBase + _index * BufferStep;
            int ptr = ReadPortReg(0x28);
            for (int i = 0; i < _rxQueue.Count; i++)
                _mem[(offset + ((ptr + i) & BufferMask)) & 0xFFFF] = _rxQueue[i];
            WritePortReg(0x26, (ushort)_rxQueue.Count);
        }

        private void UpdateRxSize() => CopyRxToChip();

        private ushort BaseReg() => (ushort)(0x0400 + _index * 0x0100);

        private ushort ReadPortReg(int reg) => ReadPort((ushort)(BaseReg() + reg));

        private void WritePortReg(int reg, ushort value)
        {
            ushort addr = (ushort)(BaseReg() + reg);
            _mem[addr] = (byte)(value >> 8);
            _mem[addr + 1] = (byte)value;
        }

        private void WriteStatus(byte status) => _mem[BaseReg() + 3] = status;

        private ushort ReadPort(ushort address) =>
            (ushort)(_mem[address] << 8 | _mem[address + 1]);
    }
}

using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>FreHD-style WD1010 task file and DS1307 clock at C2h, C4h, and C8h..CFh.</summary>
public sealed class FreHdDevice : IIoDevice
{
    public const byte PortClock = 0xC2;
    public const byte PortCmd2 = 0xC4;
    public const byte PortData = 0xC8;
    public const byte PortError = 0xC9;
    public const byte PortCount = 0xCA;
    public const byte PortSector = 0xCB;
    public const byte PortCylLo = 0xCC;
    public const byte PortCylHi = 0xCD;
    public const byte PortSdh = 0xCE;
    public const byte PortStatus = 0xCF;

    private const byte CmdRead = 0x20;
    private const byte CmdWrite = 0x30;
    private const byte CmdGetTime = 0x01;
    private const byte StatusIdle = 0x58;
    private const byte StatusWriteDone = 0x78;

    private readonly byte[] _image;
    private readonly Action<byte[]>? _writeBack;
    private bool _dirty;

    private byte _sdhEcho = 0x07;
    private byte _status = StatusIdle;
    private byte _error;
    private readonly byte[] _lba = new byte[3];
    private int _dataIndex;
    private bool _writeCommand;
    private bool _clockRead;
    private readonly byte[] _clock = [0x00, 0x00, 0x00, 0x00, 0x01, 0x01];

    public FreHdDevice(byte[] image, Action<byte[]>? writeBack = null, bool haltClock = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        _image = image;
        _writeBack = writeBack;
        if (haltClock)
            _clock[0] = 0x80;
    }

    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, PortClock);
        bus.Register(this, PortCmd2);
        bus.Register(this, PortData, PortStatus);
    }

    public void Flush()
    {
        if (!_dirty || _writeBack is null)
            return;

        _writeBack(_image);
        _dirty = false;
    }

    public byte In(byte port) => port switch
    {
        PortClock when _clockRead => _clock[Math.Min(_dataIndex++, _clock.Length - 1)],
        PortData => ReadDataByte(),
        PortError => _error,
        PortStatus => _status,
        PortSdh => _sdhEcho,
        _ => 0xFF,
    };

    public void Out(byte port, byte value)
    {
        switch (port)
        {
            case PortSdh:
                _sdhEcho = value;
                _lba[0] = (byte)((_lba[0] & 0x1F) | ((value & 7) << 5));
                break;
            case PortCmd2 when value == CmdGetTime:
                _clockRead = true;
                _dataIndex = 0;
                break;
            case PortCount:
                _dataIndex = 0;
                _clockRead = false;
                break;
            case PortSector:
                _lba[0] = (byte)((_lba[0] & 0xE0) | ((value - 1) & 0x1F));
                break;
            case PortCylLo:
                _lba[1] = value;
                break;
            case PortCylHi:
                _lba[2] = value;
                break;
            case PortStatus:
                StartCommand(value);
                break;
            case PortData when _writeCommand:
                WriteDataByte(value);
                break;
        }
    }

    private void StartCommand(byte command)
    {
        _writeCommand = command == CmdWrite;
        _dataIndex = 0;
        _error = 0;
        _status = StatusIdle;
        int block = BlockNumber();
        if (block < 0 || block * 256L + 256 > _image.Length)
        {
            _error = 0x01;
            return;
        }

        if (!_writeCommand)
            return;
    }

    private int BlockNumber() => _lba[0] | (_lba[1] << 8) | (_lba[2] << 16);

    private byte ReadDataByte()
    {
        int block = BlockNumber();
        if (block < 0)
            return 0xFF;

        int offset = block * 256 + _dataIndex;
        byte value = offset < _image.Length ? _image[offset] : (byte)0;
        _dataIndex++;
        return value;
    }

    private void WriteDataByte(byte value)
    {
        int block = BlockNumber();
        if (block < 0)
            return;

        int offset = block * 256 + _dataIndex;
        if (offset < _image.Length)
        {
            if (_image[offset] != value)
            {
                _image[offset] = value;
                _dirty = true;
            }
        }

        _dataIndex++;
        if (_dataIndex >= 256)
            _status = StatusWriteDone;
    }
}

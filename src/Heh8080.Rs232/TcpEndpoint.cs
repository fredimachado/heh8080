using System.Net;
using System.Net.Sockets;
using Heh8080.Devices;

namespace Heh8080.Rs232;

/// <summary>Raw TCP cable. Carrier, CTS, and DSR follow the socket. Ring stays clear.</summary>
internal sealed class TcpEndpoint : ISerialEndpoint
{
    private readonly object _gate = new();
    private readonly TcpListener? _listener;
    private readonly CancellationTokenSource? _stop;
    private readonly Task? _accept;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _disposed;

    private TcpEndpoint(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    private TcpEndpoint(TcpListener listener)
    {
        _listener = listener;
        _stop = new CancellationTokenSource();
        _accept = AcceptLoop();
    }

    public static TcpEndpoint Connect(string host, int port)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            client.ConnectAsync(host, port, timeout.Token).GetAwaiter().GetResult();
            return new TcpEndpoint(client);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            client.Dispose();
            throw new IOException($"RS-232 TCP {host}:{port} failed: {ex.Message}", ex);
        }
    }

    public static TcpEndpoint Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new IOException($"RS-232 listen {port} failed: {ex.Message}", ex);
        }

        return new TcpEndpoint(listener);
    }

    public ModemInputs Inputs
    {
        get
        {
            lock (_gate)
                return _stream is null ? ModemInputs.Idle : ModemInputs.Online;
        }
    }

    public bool TryRead(out byte value)
    {
        lock (_gate)
        {
            if (_stream is not { DataAvailable: true })
            {
                value = 0;
                return false;
            }

            try
            {
                int read = _stream.ReadByte();
                if (read < 0)
                {
                    Drop();
                    value = 0;
                    return false;
                }

                value = (byte)read;
                return true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Drop();
                value = 0;
                return false;
            }
        }
    }

    public void Write(byte value)
    {
        lock (_gate)
        {
            if (_stream is null)
                return;

            try
            {
                _stream.WriteByte(value);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Drop();
            }
        }
    }

    public void AdvanceCycles(long cycles)
    {
    }

    public void Dispose()
    {
        Task? accept;
        CancellationTokenSource? stop;
        TcpListener? listener;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            Drop();
            accept = _accept;
            stop = _stop;
            listener = _listener;
        }

        stop?.Cancel();
        listener?.Stop();
        if (accept is null)
            return;

        try
        {
            accept.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (_stop is not null && !_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener!.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                client.NoDelay = true;
                lock (_gate)
                {
                    if (_disposed || _client is not null)
                    {
                        client.Dispose();
                        continue;
                    }

                    _client = client;
                    _stream = client.GetStream();
                }
            }
        }
        catch (Exception) when (_stop is not null && _stop.IsCancellationRequested)
        {
        }
    }

    private void Drop()
    {
        _stream = null;
        _client?.Dispose();
        _client = null;
    }
}

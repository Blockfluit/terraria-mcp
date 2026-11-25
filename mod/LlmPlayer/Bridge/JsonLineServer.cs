using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LlmPlayer.Core.Bridge;

namespace LlmPlayer.Bridge;

/// <summary>
/// Newline-delimited JSON over TCP. Background threads only: accept, read+parse, write.
/// It never touches game state; the main thread drains <see cref="Inbound"/> and calls <see cref="Send"/>.
/// One client at a time — a new connection replaces (closes) the old one.
/// </summary>
public sealed class JsonLineServer
{
    private const int MaxOutbound = 4096;

    private readonly Action<string> _log;
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private Connection? _current; // accessed via Volatile/Interlocked
    private volatile bool _running;

    public ConcurrentQueue<(int Id, string Method, JsonElement Params)> Inbound { get; } = new();

    public string? Endpoint { get; private set; }
    public bool HasClient => Volatile.Read(ref _current) is { Closed: false };

    public JsonLineServer(Action<string> log) => _log = log;

    /// <summary>Binds synchronously (throws SocketException if the port is taken), then accepts on a background thread.</summary>
    public void Start(string bind, int port)
    {
        if (_running) return;
        if (!IPAddress.TryParse(bind, out var address))
        {
            _log($"invalid BindAddress '{bind}', using 0.0.0.0");
            address = IPAddress.Any;
        }
        _listener = new TcpListener(address, port);
        _listener.Start();
        _running = true;
        Endpoint = $"{address}:{port}";
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LlmPlayer.Accept" };
        _acceptThread.Start();
        _log($"listening on {Endpoint}");
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        Interlocked.Exchange(ref _current, null)?.Close();
        Inbound.Clear();
    }

    /// <summary>Serialize (camelCase) and queue for the current client. Dropped if nobody is connected.</summary>
    public void Send(object message) => SendLine(message as string ?? WireProtocol.Serialize(message));

    /// <summary>Queue an already-serialized JSON line (no trailing newline).</summary>
    public void SendLine(string line) => Volatile.Read(ref _current)?.Enqueue(line);

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                client = _listener!.AcceptTcpClient();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (_running) _log($"accept failed: {e.Message}");
                return;
            }

            client.NoDelay = true;
            var conn = new Connection(this, client);
            var old = Interlocked.Exchange(ref _current, conn);
            if (old != null)
            {
                _log("new client replaces the previous one");
                old.Close();
            }
            // Requests still queued from the old client must not be answered to the new one.
            Inbound.Clear();
            _log($"client connected from {client.Client.RemoteEndPoint}");
            conn.Start();
        }
    }

    private sealed class Connection
    {
        private readonly JsonLineServer _server;
        private readonly TcpClient _client;
        private readonly BlockingCollection<string> _out = new(MaxOutbound);
        private int _closed;

        public bool Closed => Volatile.Read(ref _closed) != 0;

        public Connection(JsonLineServer server, TcpClient client)
        {
            _server = server;
            _client = client;
        }

        public void Start()
        {
            new Thread(ReadLoop) { IsBackground = true, Name = "LlmPlayer.Read" }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "LlmPlayer.Write" }.Start();
        }

        public void Enqueue(string line)
        {
            if (Closed) return;
            try
            {
                if (!_out.TryAdd(line))
                {
                    _server._log("client not reading (outbound queue full); disconnecting");
                    Close();
                }
            }
            catch (InvalidOperationException) { /* completed */ }
        }

        private void ReadLoop()
        {
            try
            {
                using var reader = new StreamReader(_client.GetStream(), new UTF8Encoding(false));
                string? line;
                while (!Closed && (line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var req = WireProtocol.ParseRequest(line);
                    if (req.Ok)
                    {
                        if (ReferenceEquals(Volatile.Read(ref _server._current), this))
                            _server.Inbound.Enqueue((req.Id!.Value, req.Method, req.Params));
                    }
                    else
                    {
                        Enqueue(req.ErrorLine!); // malformed: answer directly, no game state involved
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // connection dropped
            }
            finally
            {
                if (!Closed) _server._log("client disconnected");
                Close();
            }
        }

        private void WriteLoop()
        {
            try
            {
                var stream = _client.GetStream();
                foreach (var line in _out.GetConsumingEnumerable())
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // connection dropped
            }
            finally
            {
                Close();
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _out.CompleteAdding();
            try { _client.Close(); } catch { /* ignore */ }
            Interlocked.CompareExchange(ref _server._current, null, this);
        }
    }
}

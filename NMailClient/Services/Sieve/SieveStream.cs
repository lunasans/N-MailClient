using System.IO;
using System.Text;

namespace NMailClient.Services.Sieve;

/// <summary>
/// Gepufferter Leser und Schreiber für das ManageSieve-Protokoll.
///
/// Eigene Klasse statt <see cref="StreamReader"/>, weil das Protokoll zeilen-
/// <b>und</b> byteweise gelesen werden muss: nach einer Literal-Ankündigung
/// <c>{n+}</c> folgen genau n Bytes. Ein StreamReader hätte davon schon Teile in
/// seinem eigenen Puffer und liefe aus dem Tritt.
/// </summary>
public sealed class SieveStream(Stream stream) : IAsyncDisposable
{
    private readonly byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    /// <summary>Der zugrunde liegende Datenstrom – für die Umstellung auf TLS.</summary>
    public Stream Inner { get; private set; } = stream;

    /// <summary>Nach STARTTLS auf den verschlüsselten Strom umstellen.</summary>
    public void Upgrade(Stream secure)
    {
        // Alles Gepufferte gehört zur unverschlüsselten Phase und ist verbraucht.
        _start = _end = 0;
        Inner = secure;
    }

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await Inner.WriteAsync(bytes, ct);
        await Inner.FlushAsync(ct);
    }

    /// <summary>Rohbytes anhängen – für den Inhalt eines Literals.</summary>
    public async Task WriteRawAsync(string content, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await Inner.WriteAsync(bytes, ct);
        await Inner.WriteAsync("\r\n"u8.ToArray(), ct);
        await Inner.FlushAsync(ct);
    }

    /// <summary>
    /// Eine Zeile bis CRLF. Null bedeutet, dass die Gegenseite aufgelegt hat.
    /// </summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var line = new MemoryStream();

        while (true)
        {
            if (_start >= _end && !await FillAsync(ct))
                return line.Length == 0 ? null : Decode(line);

            var b = _buffer[_start++];

            if (b == '\n')
            {
                // Ein vorangehendes CR gehört nicht zum Inhalt.
                var bytes = line.ToArray();
                if (bytes.Length > 0 && bytes[^1] == '\r')
                    return Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
                return Encoding.UTF8.GetString(bytes);
            }

            line.WriteByte(b);
        }
    }

    private static string Decode(MemoryStream buffer)
        => Encoding.UTF8.GetString(buffer.ToArray());

    /// <summary>Genau so viele Bytes lesen, wie das Literal angekündigt hat.</summary>
    public async Task<string> ReadExactlyAsync(int count, CancellationToken ct)
    {
        if (count == 0) return "";

        var result = new byte[count];
        var filled = 0;

        while (filled < count)
        {
            if (_start >= _end && !await FillAsync(ct))
                throw new IOException("Verbindung endete mitten im Literal.");

            var take = Math.Min(count - filled, _end - _start);
            Array.Copy(_buffer, _start, result, filled, take);

            _start += take;
            filled += take;
        }

        return Encoding.UTF8.GetString(result);
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        _start = 0;
        _end = await Inner.ReadAsync(_buffer, ct);
        return _end > 0;
    }

    public async ValueTask DisposeAsync()
    {
        try { await Inner.DisposeAsync(); }
        catch (IOException) { /* schon zu – folgenlos */ }
    }
}

using System.Text;
using TomYum.Irc;

namespace TomYum.Server.Irc;

/// <summary>
/// Reads an IRC connection line by line, holding at most one line's worth: a line is at most
/// <see cref="IrcLine.MaxLength"/> bytes (as IRC has it), and a client that sends more without a
/// newline is cut off (<see cref="ReadLineAsync"/> gives null) instead of being buffered without end.
/// </summary>
internal sealed class IrcLineReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[IrcLine.MaxLength * 4];
    private int _start;
    private int _end;

    public IrcLineReader(Stream stream) => _stream = stream;

    /// <summary>The next line (without its CR LF); null when the connection ended or the line is too long.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var length = newline - _start;
                if (length > 0 && _buffer[newline - 1] == '\r')
                    length--;
                if (length > IrcLine.MaxLength)
                    return null;
                var line = Encoding.UTF8.GetString(_buffer, _start, length);
                _start = newline + 1;
                return line;
            }
            if (_end - _start > IrcLine.MaxLength + 1)
                return null; // no newline in a whole line's worth: not an IRC client

            // Room for the rest of the line at the end of the buffer.
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return null;
            _end += read;
        }
    }
}

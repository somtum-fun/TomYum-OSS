using System.Buffers.Binary;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// An .osz on its way to an old build, with each entry's "version needed to extract" as old osu!'s
/// zip library (SharpZipLib, 2008–2015) can read it. Sets repacked in 2014 say 6.3 in their local
/// headers (their central directory says 2.0, and they're plain stored or deflated): old osu! stops
/// at the first entry ("version required to extract this entry not supported (63)"), so the set
/// imports without its .osu files and never shows in song select. Plain entries (stored or deflated,
/// not encrypted, not Zip64) that say more than 4.5, or 2.1 to 4.4, say 2.0 instead; nothing else
/// changes, so the length stays the same. Entries are found by walking the headers in order; an
/// entry whose sizes follow its data (flag bit 3) ends the walk and the rest passes as it is.
/// </summary>
public sealed class OszVersionFixStream : Stream
{
    private const uint LocalHeader = 0x04034b50;
    private const uint CentralHeader = 0x02014b50;
    private const ushort Readable = 20;

    private readonly Stream _inner;
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private long _position;    // offset in the archive of _buffer[_start]
    private long _nextHeader;  // offset in the archive of the next header to look at
    private bool _walking = true;
    private bool _innerDone;

    public OszVersionFixStream(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadCoreAsync(buffer.AsMemory(offset, count), sync: true, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(buffer, sync: false, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadCoreAsync(buffer.AsMemory(offset, count), sync: false, cancellationToken).AsTask();

    private async ValueTask<int> ReadCoreAsync(Memory<byte> destination, bool sync, CancellationToken cancellationToken)
    {
        if (destination.Length == 0)
            return 0;
        if (_start == _end && !await FillAsync(1, sync, cancellationToken).ConfigureAwait(false))
            return 0;

        // Fix every header that starts in what's buffered before handing any of it out.
        while (_walking && _nextHeader >= _position && _nextHeader < _position + (_end - _start))
        {
            if (!await FixHeaderAsync(sync, cancellationToken).ConfigureAwait(false))
                _walking = false;
        }

        var count = Math.Min(destination.Length, _end - _start);
        _buffer.AsMemory(_start, count).CopyTo(destination);
        _start += count;
        _position += count;
        return count;
    }

    /// <summary>Fixes the header at <see cref="_nextHeader"/> and finds the next; false when the walk ends.</summary>
    private async ValueTask<bool> FixHeaderAsync(bool sync, CancellationToken cancellationToken)
    {
        var at = (int)(_nextHeader - _position);
        if (!await FillAsync(at + 4, sync, cancellationToken).ConfigureAwait(false))
            return false;
        var signature = BinaryPrimitives.ReadUInt32LittleEndian(Span(at, 4));
        var fixedSize = signature switch { LocalHeader => 30, CentralHeader => 46, _ => 0 };
        if (fixedSize == 0 || !await FillAsync(at + fixedSize, sync, cancellationToken).ConfigureAwait(false))
            return false;

        var local = signature == LocalHeader;
        var header = Span(at, fixedSize);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[(local ? 6 : 8)..]);
        var method = BinaryPrimitives.ReadUInt16LittleEndian(header[(local ? 8 : 10)..]);
        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[(local ? 18 : 20)..]);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[(local ? 26 : 28)..]);
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[(local ? 28 : 30)..]);
        var commentLength = local ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
        if (!await FillAsync(at + fixedSize + nameLength + extraLength, sync, cancellationToken).ConfigureAwait(false))
            return false;

        at = (int)(_nextHeader - _position); // the buffer may have moved
        var versionAt = at + (local ? 4 : 6);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(Span(versionAt, 2));
        var zip64 = compressedSize == uint.MaxValue || HasZip64Extra(Span(at + fixedSize + nameLength, extraLength));
        if ((method == 0 || method == 8) && (flags & 0x41) == 0 && !zip64 && (version > 45 || (version > 20 && version < 45)))
            BinaryPrimitives.WriteUInt16LittleEndian(Span(versionAt, 2), Readable);

        if (local && (flags & 0x08) != 0)
            return false; // its size comes after the data: the next header can't be found from here
        _nextHeader += fixedSize + nameLength + extraLength + (local ? compressedSize : (long)commentLength);
        return true;
    }

    private static bool HasZip64Extra(Span<byte> extra)
    {
        for (var i = 0; i + 4 <= extra.Length;)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra[i..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(i + 2)..]);
            if (id == 0x0001)
                return true;
            i += 4 + size;
        }
        return false;
    }

    private Span<byte> Span(int offset, int length) => _buffer.AsSpan(_start + offset, length);

    /// <summary>Makes at least <paramref name="count"/> bytes from <see cref="_position"/> buffered; false at the end of the archive.</summary>
    private async ValueTask<bool> FillAsync(int count, bool sync, CancellationToken cancellationToken)
    {
        while (_end - _start < count)
        {
            if (_innerDone)
                return false;
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            if (_buffer.Length < count)
                Array.Resize(ref _buffer, Math.Max(count, _buffer.Length * 2));
            if (_end == _buffer.Length)
                Array.Resize(ref _buffer, _buffer.Length * 2);
            var read = sync
                ? _inner.Read(_buffer, _end, _buffer.Length - _end)
                : await _inner.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                _innerDone = true;
            _end += read;
        }
        return true;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}

namespace ReAnimated.Codecs.Rp6l;

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _source;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private long _position;
    private bool _disposed;

    public BoundedReadStream(
        Stream source,
        long start,
        long length,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(
                "The source stream must be readable and seekable.",
                nameof(source));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start > source.Length || length > source.Length - start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "The bounded range lies outside the source stream.");
        }

        _source = source;
        _start = start;
        _length = length;
        _leaveOpen = leaveOpen;
        _source.Position = _start;
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => !_disposed;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateBuffer(buffer, offset, count);
        int requested = (int)Math.Min(count, _length - _position);
        if (requested <= 0)
        {
            return 0;
        }

        EnsureSourcePosition();
        int read = _source.Read(buffer, offset, requested);
        _position += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int requested = (int)Math.Min(buffer.Length, _length - _position);
        if (requested <= 0)
        {
            return 0;
        }

        EnsureSourcePosition();
        int read = _source.Read(buffer[..requested]);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int requested = (int)Math.Min(buffer.Length, _length - _position);
        if (requested <= 0)
        {
            return 0;
        }

        EnsureSourcePosition();
        int read = await _source.ReadAsync(
            buffer[..requested],
            cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0 || target > _length)
        {
            throw new IOException("Attempted to seek outside a bounded stream.");
        }

        _position = target;
        _source.Position = checked(_start + target);
        return _position;
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing && !_leaveOpen)
        {
            _source.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed && !_leaveOpen)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }

        _disposed = true;
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The buffer range is invalid.");
        }
    }

    private void EnsureSourcePosition()
    {
        long expected = checked(_start + _position);
        if (_source.Position != expected)
        {
            _source.Position = expected;
        }
    }
}

internal sealed class ConcatenatedReadStream : Stream
{
    private readonly IReadOnlyList<Stream> _streams;
    private int _index;
    private bool _disposed;

    public ConcatenatedReadStream(IReadOnlyList<Stream> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        _streams = streams;
        Length = streams.Aggregate(
            0L,
            static (total, stream) => checked(total + stream.Length));
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position { get; set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int total = 0;
        while (count > 0 && _index < _streams.Count)
        {
            int read = _streams[_index].Read(buffer, offset, count);
            if (read == 0)
            {
                _index++;
                continue;
            }

            total += read;
            offset += read;
            count -= read;
            Position += read;
        }

        return total;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int total = 0;
        while (buffer.Length > 0 && _index < _streams.Count)
        {
            int read = await _streams[_index].ReadAsync(
                buffer,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                _index++;
                continue;
            }

            total += read;
            Position += read;
            buffer = buffer[read..];
        }

        return total;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            foreach (Stream stream in _streams)
            {
                stream.Dispose();
            }
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            foreach (Stream stream in _streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        _disposed = true;
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

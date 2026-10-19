using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Write-only stream that collects bytes in chunks rented from <see cref="ArrayPool{T}.Shared"/>, so building a large
/// payload allocates only the exact-size array returned by <see cref="ToArray"/>.
/// </summary>
/// <remarks>
/// Not thread safe. Dispose returns the chunks; the stream must not be used afterwards.
/// </remarks>
internal sealed class PooledChunkWriteStream : Stream
{
    private const int FirstChunkSize = 16 * 1024;
    private const int MaxChunkSize = 1024 * 1024;

    private readonly List<byte[]> chunks = [];
    private byte[]? current;
    private int currentUsed;
    private long length;
    private bool disposed;

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => !this.disposed;

    /// <inheritdoc/>
    public override long Length => this.length;

    /// <inheritdoc/>
    public override long Position
    {
        get => this.length;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Copies everything written so far into a new array of exactly <see cref="Length"/> bytes.
    /// </summary>
    /// <returns>The written bytes.</returns>
    public byte[] ToArray()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        var result = new byte[checked((int)this.length)];
        var offset = 0;
        foreach (var chunk in this.chunks)
        {
            var count = chunk == this.current ? this.currentUsed : chunk.Length;
            Buffer.BlockCopy(chunk, 0, result, offset, count);
            offset += count;
        }

        return result;
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        this.Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        while (!buffer.IsEmpty)
        {
            if (this.current is null || this.currentUsed == this.current.Length)
                this.AddChunk();

            var n = Math.Min(buffer.Length, this.current!.Length - this.currentUsed);
            buffer[..n].CopyTo(this.current.AsSpan(this.currentUsed));
            this.currentUsed += n;
            this.length += n;
            buffer = buffer[n..];
        }
    }

    /// <inheritdoc/>
    public override void WriteByte(byte value) => this.Write(new ReadOnlySpan<byte>(in value));

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.disposed)
        {
            this.disposed = true;
            foreach (var chunk in this.chunks)
                ArrayPool<byte>.Shared.Return(chunk);
            this.chunks.Clear();
            this.current = null;
            this.currentUsed = 0;
        }

        base.Dispose(disposing);
    }

    private void AddChunk()
    {
        var size = this.current is null ? FirstChunkSize : Math.Min(this.current.Length * 2, MaxChunkSize);
        this.current = ArrayPool<byte>.Shared.Rent(size);
        this.currentUsed = 0;
        this.chunks.Add(this.current);
    }
}

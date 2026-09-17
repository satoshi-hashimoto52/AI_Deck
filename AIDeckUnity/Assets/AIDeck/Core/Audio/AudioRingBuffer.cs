using System;
using System.Threading;

namespace AIDeck.Core.Audio
{
    /// <summary>
    /// Single-producer / single-consumer lock-free float ring buffer.
    ///
    /// The audio callback is the producer and must never block, allocate, or take a lock
    /// (NFR-001): a stall there is an audible dropout. The recorder's writer thread is the
    /// consumer. When the consumer falls behind, the producer drops the newest block and
    /// counts it rather than blocking or growing — a bounded queue is also what NFR-004
    /// asks for on the network side.
    /// </summary>
    public sealed class AudioRingBuffer
    {
        private readonly float[] _buffer;
        private readonly int _capacity;
        private int _writeIndex;
        private int _readIndex;
        private long _overflowCount;

        public AudioRingBuffer(int capacitySamples)
        {
            if (capacitySamples < 2)
            {
                capacitySamples = 2;
            }

            // One slot is always left empty so that full and empty are distinguishable
            // without a separate count that both threads would have to update.
            _capacity = capacitySamples + 1;
            _buffer = new float[_capacity];
        }

        /// <summary>Usable capacity in samples.</summary>
        public int Capacity => _capacity - 1;

        /// <summary>Samples currently waiting to be read.</summary>
        public int Available
        {
            get
            {
                var write = Volatile.Read(ref _writeIndex);
                var read = Volatile.Read(ref _readIndex);
                var diff = write - read;
                return diff < 0 ? diff + _capacity : diff;
            }
        }

        public int FreeSpace => Capacity - Available;

        /// <summary>How many samples have been dropped because the consumer fell behind.</summary>
        public long OverflowCount => Interlocked.Read(ref _overflowCount);

        /// <summary>
        /// Producer side. Writes as much of the block as fits; returns the number of samples
        /// written. Any shortfall is added to <see cref="OverflowCount"/>.
        /// </summary>
        public int Write(float[] source, int offset, int count)
        {
            if (source == null || count <= 0 || offset < 0 || offset >= source.Length)
            {
                return 0;
            }

            if (offset + count > source.Length)
            {
                count = source.Length - offset;
            }

            var write = _writeIndex;
            var read = Volatile.Read(ref _readIndex);
            var free = read - write - 1;
            if (free < 0)
            {
                free += _capacity;
            }

            var toWrite = count < free ? count : free;
            if (toWrite < count)
            {
                Interlocked.Add(ref _overflowCount, count - toWrite);
            }

            for (var i = 0; i < toWrite; i++)
            {
                _buffer[write] = source[offset + i];
                write++;
                if (write >= _capacity)
                {
                    write = 0;
                }
            }

            Volatile.Write(ref _writeIndex, write);
            return toWrite;
        }

        /// <summary>Consumer side. Reads up to <paramref name="count"/> samples.</summary>
        public int Read(float[] destination, int offset, int count)
        {
            if (destination == null || count <= 0 || offset < 0 || offset >= destination.Length)
            {
                return 0;
            }

            if (offset + count > destination.Length)
            {
                count = destination.Length - offset;
            }

            var read = _readIndex;
            var write = Volatile.Read(ref _writeIndex);
            var available = write - read;
            if (available < 0)
            {
                available += _capacity;
            }

            var toRead = count < available ? count : available;
            for (var i = 0; i < toRead; i++)
            {
                destination[offset + i] = _buffer[read];
                read++;
                if (read >= _capacity)
                {
                    read = 0;
                }
            }

            Volatile.Write(ref _readIndex, read);
            return toRead;
        }

        /// <summary>Discards pending samples. Only safe when the producer is known to be idle.</summary>
        public void Clear()
        {
            Volatile.Write(ref _readIndex, 0);
            Volatile.Write(ref _writeIndex, 0);
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        public void ResetOverflowCount() => Interlocked.Exchange(ref _overflowCount, 0L);
    }
}

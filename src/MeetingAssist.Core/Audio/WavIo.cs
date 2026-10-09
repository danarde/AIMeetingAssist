using System.Buffers.Binary;

namespace MeetingAssist.Core.Audio;

/// <summary>
/// Minimal WAV read/write for 16-bit PCM. Used to persist fixtures and to wrap raw PCM
/// segments for upload, since transcription APIs want a container, not bare samples.
/// </summary>
public static class WavIo
{
    private const int HeaderSize = 44;

    /// <summary>Wraps raw PCM in a WAV container in memory. No file involved.</summary>
    public static byte[] WrapPcm(ReadOnlySpan<byte> pcm, int sampleRate = AudioFormat.SampleRate, int channels = AudioFormat.Channels)
    {
        var result = new byte[HeaderSize + pcm.Length];
        WriteHeader(result, pcm.Length, sampleRate, channels);
        pcm.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }

    private static void WriteHeader(Span<byte> dst, int dataBytes, int sampleRate, int channels)
    {
        const int bits = AudioFormat.Bits;
        var byteRate = sampleRate * channels * bits / 8;
        var blockAlign = (short)(channels * bits / 8);

        "RIFF"u8.CopyTo(dst);
        BinaryPrimitives.WriteInt32LittleEndian(dst[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(dst[8..]);
        "fmt "u8.CopyTo(dst[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(dst[16..], 16);          // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(dst[20..], 1);           // PCM
        BinaryPrimitives.WriteInt16LittleEndian(dst[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(dst[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(dst[28..], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(dst[32..], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(dst[34..], bits);
        "data"u8.CopyTo(dst[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(dst[40..], dataBytes);
    }

    /// <summary>
    /// Reads a 16-bit PCM WAV and returns the raw sample bytes plus its format.
    /// Handles arbitrary chunk ordering (LIST/fact chunks before data are common).
    /// </summary>
    public static (byte[] Pcm, int SampleRate, int Channels) ReadPcm(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException($"Not a RIFF/WAVE file: {path}");

        int sampleRate = 0, channels = 0, bits = 0;
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4));
            var body = pos + 8;
            if (size < 0 || body + size > bytes.Length) size = bytes.Length - body;

            if (id.SequenceEqual("fmt "u8))
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 14));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (bits != 16)
                    throw new NotSupportedException($"Only 16-bit PCM is supported; {path} is {bits}-bit.");
                return (bytes.AsSpan(body, size).ToArray(), sampleRate, channels);
            }

            pos = body + size + (size % 2); // chunks are word-aligned
        }
        throw new InvalidDataException($"No data chunk found in {path}");
    }
}

/// <summary>Streams 16-bit PCM to a .wav file, patching the header sizes on dispose.</summary>
public sealed class WavFileWriter : IDisposable
{
    private readonly FileStream _stream;
    private int _dataBytes;

    public WavFileWriter(string path, int sampleRate = AudioFormat.SampleRate, int channels = AudioFormat.Channels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _stream = File.Create(path);
        SampleRate = sampleRate;
        Channels = channels;
        _stream.Write(new byte[44]); // placeholder, rewritten on dispose
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int DataBytes => _dataBytes;

    public void Write(ReadOnlySpan<byte> pcm)
    {
        _stream.Write(pcm);
        _dataBytes += pcm.Length;
    }

    public void Dispose()
    {
        if (!_stream.CanWrite) return;
        _stream.Position = 0;
        var header = WavIo.WrapPcm(ReadOnlySpan<byte>.Empty, SampleRate, Channels);
        // WrapPcm wrote sizes for an empty payload; patch in the real ones.
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + _dataBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), _dataBytes);
        _stream.Write(header);
        _stream.Dispose();
    }
}

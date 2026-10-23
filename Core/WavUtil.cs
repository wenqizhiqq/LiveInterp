using System.Buffers.Binary;
using System.Text;

namespace LiveInterp.Core;

public static class WavUtil
{
    public static byte[] Build16kMono(float[] samples)
    {
        const int sampleRate = 16000;
        const short channels = 1;
        const short bits = 16;
        int dataLength = samples.Length * 2;

        var buffer = new byte[44 + dataLength];
        WriteAscii(buffer, 0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 36 + dataLength);
        WriteAscii(buffer, 8, "WAVE");
        WriteAscii(buffer, 12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), sampleRate * channels * bits / 8);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(32), (short)(channels * bits / 8));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(34), bits);
        WriteAscii(buffer, 36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40), dataLength);

        for (int i = 0; i < samples.Length; i++)
        {
            short value = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(44 + i * 2), value);
        }
        return buffer;
    }

    private static void WriteAscii(byte[] target, int offset, string text)
    {
        Encoding.ASCII.GetBytes(text, target.AsSpan(offset));
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace OpenTapo.Tapo;

internal static class Bytes
{
    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    public static string Sha256Hex(string value) => Convert.ToHexString(SHA256.HashData(Utf8(value)));

    public static string Md5Hex(string value) => Convert.ToHexString(MD5.HashData(Utf8(value)));

    public static byte[] BigEndian(int value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        return buffer;
    }
}

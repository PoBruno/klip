using System.Security.Cryptography;
using System.Text;

namespace Klip.Core.Common;

public static class HashUtil
{
    public static string Sha256Hex(byte[] data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256Hex(string text) =>
        Sha256Hex(Encoding.UTF8.GetBytes(text));
}

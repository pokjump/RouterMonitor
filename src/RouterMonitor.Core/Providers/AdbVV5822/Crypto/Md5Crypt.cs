using System.Security.Cryptography;
using System.Text;

namespace RouterMonitor.Core.Providers.AdbVV5822.Crypto;

/// <summary>
/// Port of the router's own /js/md5_crypt.js (itself a JS port of FreeBSD libcrypt's md5_crypt),
/// used only because the ADB "epicentro" login form hashes the password client-side before
/// POSTing — despite firmware VV5822_NETIA_7.6.0.0010 having no server-side TLS, the login
/// form still HMACs a $1$-style MD5 crypt digest with a per-page nonce. This must match the
/// JS bit-for-bit or the router will reject the login. See AdbVV5822LoginTests for reference
/// vectors captured directly from the live router's JS.
/// </summary>
internal static class Md5Crypt
{
    private const string Itoa64 = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const string Magic = "$1$";

    public static string Crypt(string key, string salt)
    {
        if (salt.StartsWith(Magic, StringComparison.Ordinal))
            salt = salt[Magic.Length..];

        var dollarIndex = salt.IndexOf('$');
        var saltLen = dollarIndex is < 0 or > 8 ? 8 : dollarIndex;
        salt = salt[..Math.Min(saltLen, salt.Length)];

        var keyBytes = ToBytes(key);
        var saltBytes = ToBytes(salt);

        using var md5 = MD5.Create();

        var hash = md5.ComputeHash(Join(keyBytes, saltBytes, keyBytes));

        using var stream = new MemoryStream();
        stream.Write(keyBytes);
        stream.Write(ToBytes(Magic));
        stream.Write(saltBytes);

        for (var i = key.Length; i > 0; i -= 16)
            stream.Write(i >= 16 ? hash : hash.AsSpan(0, i));

        for (var i = key.Length; i != 0; i >>= 1)
        {
            if ((i & 1) != 0)
                stream.WriteByte(0x00);
            else
                stream.WriteByte(keyBytes.Length > 0 ? keyBytes[0] : (byte)0);
        }

        hash = md5.ComputeHash(stream.ToArray());

        for (var i = 0; i < 1000; i++)
        {
            using var round = new MemoryStream();
            if ((i & 1) != 0) round.Write(keyBytes); else round.Write(hash);
            if (i % 3 != 0) round.Write(saltBytes);
            if (i % 7 != 0) round.Write(keyBytes);
            if ((i & 1) != 0) round.Write(hash); else round.Write(keyBytes);
            hash = md5.ComputeHash(round.ToArray());
        }

        // hash += hash.charAt(5) — extend the 16-byte digest with a 17th byte copied from index 5.
        var extended = new byte[17];
        hash.CopyTo(extended, 0);
        extended[16] = hash[5];

        var result = new StringBuilder();
        result.Append(Magic).Append(salt).Append('$');

        for (var i = 0; i < 5; i++)
        {
            var value = (extended[i] << 16) | (extended[i + 6] << 8) | extended[i + 12];
            result.Append(Md5To64(value, 4));
        }

        result.Append(Md5To64(extended[11], 2));

        return result.ToString();
    }

    private static string Md5To64(int value, int n)
    {
        var sb = new StringBuilder(n);
        for (var i = 0; i < n; i++)
        {
            sb.Append(Itoa64[value & 0x3f]);
            value >>= 6;
        }

        return sb.ToString();
    }

    /// <summary>
    /// The router's md5.js runs with chrsz=8 (one byte per JS char, truncated to 8 bits via
    /// `& mask`), so each character maps to exactly one byte regardless of code point — the
    /// same semantics as Latin-1/code-page-437-style byte truncation, not UTF-8.
    /// </summary>
    private static byte[] ToBytes(string s)
    {
        var bytes = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
            bytes[i] = (byte)s[i];
        return bytes;
    }

    private static byte[] Join(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace RouterMonitor.Core.Providers.AdbVV5822.Crypto;

/// <summary>
/// Reproduces the client-side hashing done by the router's /ui/login page
/// (see login_onsubmit() in the page's inline script) so the C# client can
/// submit the same derived fields a browser would, without running JS.
/// CryptoJS treats message/key strings via its UTF8 codec, which for the
/// ASCII passwords/nonces this firmware uses is identical to plain ASCII bytes.
/// </summary>
internal static class LoginHash
{
    /// <summary>HMAC-SHA256(message, key) as lowercase hex — matches CryptoJS.HmacSHA256(message, key).toString().</summary>
    public static string HmacSha256Hex(string message, string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes the three password-derived form fields, mirroring login_onsubmit():
    ///   userPwd = HMAC(password, nonce)
    ///   code998 = code999 = HMAC(md5_crypt(password, ""), nonce)
    /// (code998/code999 start as empty hidden fields, so both use "" as the md5_crypt salt.)
    /// </summary>
    public static (string UserPwd, string Code998, string Code999) ComputeLoginFields(string password, string nonce)
    {
        var userPwd = HmacSha256Hex(password, nonce);
        var md5CryptOfPassword = Md5Crypt.Crypt(password, string.Empty);
        var codeHash = HmacSha256Hex(md5CryptOfPassword, nonce);
        return (userPwd, codeHash, codeHash);
    }
}

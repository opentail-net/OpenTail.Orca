using System.Security.Cryptography;
using System.Text;

namespace OpenTail.Orca;

/// <summary>
/// The digest of an action's arguments. It is the one function behind <see cref="ActionRecord.ArgumentsHash"/> (the first 32 hex digits
/// of SHA-256 over the UTF-8 text, as stored), the digest recorded with a reviewer's edit, and the check made when an approved action
/// is claimed. Changing it would make every stored digest wrong, so it stays as it is.
/// </summary>
public static class ArgumentDigest
{
    public static string Of(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32].ToLowerInvariant();
}

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Blocker.Core.Abstractions;

namespace Blocker.Platform.Windows.Processes;

/// <summary>
/// DPAPI with <see cref="DataProtectionScope.LocalMachine"/> and fixed application entropy: any process on this machine that knows the entropy
/// can decrypt, but the data cannot be moved to another machine. The entropy only separates our blobs from other DPAPI users; it is not a secret.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsDetox.v1");

    /// <inheritdoc/>
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var copy = plaintext.ToArray();
        try
        {
            return ProtectedData.Protect(copy, Entropy, DataProtectionScope.LocalMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    /// <inheritdoc/>
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ProtectedData.Unprotect(ciphertext.ToArray(), Entropy, DataProtectionScope.LocalMachine);
}

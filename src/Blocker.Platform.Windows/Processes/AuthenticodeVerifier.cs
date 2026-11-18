using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Blocker.Core.Ports;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography;
using Windows.Win32.Security.Cryptography.Catalog;
using Windows.Win32.Security.WinTrust;

namespace Blocker.Platform.Windows.Processes;

/// <summary>
/// WinVerifyTrust based verification that a file is signed by Microsoft. Order: embedded Authenticode signature first; when the file has none,
/// the security catalogs (how most Windows system binaries are signed) are searched by file hash and the catalog's signature is verified.
/// </summary>
/// <remarks>
/// Trust decision: WinVerifyTrust must succeed (chain builds to a root in the machine's trust store, hash matches) and the signer chain must be
/// Microsoft: leaf simple display name contains "Microsoft Windows" or "Microsoft Corporation", and the chain's root is a trusted root whose name starts with
/// "Microsoft". Revocation is not checked and no network access is made (<c>WTD_REVOKE_NONE</c>, <c>WTD_CACHE_ONLY_URL_RETRIEVAL</c>) so the check is cheap
/// and works offline. Limitation: the root is matched by name plus the system trust store, not by pinned thumbprint; an administrator who installs a
/// look-alike root already controls the machine. Results are cached per (path, last write time, length).
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class AuthenticodeVerifier : ISignatureVerifier
{
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const uint CertNameSimpleDisplayType = 4;
    private const int ErrorInsufficientBuffer = 122;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public bool IsMicrosoftSigned(string path)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists)
        {
            return false;
        }

        var stamp = new CacheEntry(info.LastWriteTimeUtc, info.Length, false);
        if (_cache.TryGetValue(info.FullName, out var cached) && cached.SameFileAs(stamp))
        {
            return cached.Result;
        }

        bool result;
        try
        {
            result = Verify(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file we cannot even read (locked exclusively, ACL) cannot be proven Microsoft-signed; deliberately not cached.
            return false;
        }

        _cache[info.FullName] = stamp with { Result = result };
        return result;
    }

    private static bool Verify(string path)
    {
        var status = VerifyEmbedded(path, out var microsoft);
        if (status == 0)
        {
            return microsoft;
        }

        return status is TrustENoSignature or TrustEProviderUnknown or TrustESubjectFormUnknown && VerifyViaCatalog(path);
    }

    private static unsafe int VerifyEmbedded(string path, out bool microsoft)
    {
        fixed (char* filePath = path)
        {
            var file = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = filePath };
            var data = NewTrustData();
            data.dwUnionChoice = WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_FILE;
            data.Anonymous.pFile = &file;
            return Run(&data, out microsoft);
        }
    }

    private static unsafe bool VerifyViaCatalog(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!PInvoke.CryptCATAdminAcquireContext2(out var rawAdmin, null, "SHA256", null))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using var admin = new CatalogAdminHandle(rawAdmin);
        var hash = CalculateHash(admin, file);
        var tag = Convert.ToHexString(hash);

        nint previous = 0;
        while (true)
        {
            var current = previous == 0
                ? PInvoke.CryptCATAdminEnumCatalogFromHash(rawAdmin, hash)
                : PInvoke.CryptCATAdminEnumCatalogFromHash(rawAdmin, hash, ref previous);
            if (current == 0)
            {
                return false;
            }

            previous = current;
            var catalog = new CATALOG_INFO { cbStruct = (uint)sizeof(CATALOG_INFO) };
            if (PInvoke.CryptCATCatalogInfoFromContext(current, ref catalog, 0) && VerifyMember(rawAdmin, catalog, path, tag, hash, file))
            {
                PInvoke.CryptCATAdminReleaseCatalogContext(rawAdmin, current, 0);
                return true;
            }
        }
    }

    private static unsafe bool VerifyMember(nint admin, in CATALOG_INFO catalog, string path, string tag, byte[] hash, SafeFileHandle file)
    {
        var catalogPath = catalog.wszCatalogFile.ToString();
        var added = false;
        file.DangerousAddRef(ref added);
        try
        {
            fixed (char* catalogFile = catalogPath, memberTag = tag, memberPath = path)
            fixed (byte* memberHash = hash)
            {
                var info = new WINTRUST_CATALOG_INFO
                {
                    cbStruct = (uint)sizeof(WINTRUST_CATALOG_INFO),
                    pcwszCatalogFilePath = catalogFile,
                    pcwszMemberTag = memberTag,
                    pcwszMemberFilePath = memberPath,
                    hMemberFile = new HANDLE(file.DangerousGetHandle()),
                    pbCalculatedFileHash = memberHash,
                    cbCalculatedFileHash = (uint)hash.Length,
                    hCatAdmin = admin,
                };
                var data = NewTrustData();
                data.dwUnionChoice = WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_CATALOG;
                data.Anonymous.pCatalog = &info;
                return Run(&data, out var microsoft) == 0 && microsoft;
            }
        }
        finally
        {
            if (added)
            {
                file.DangerousRelease();
            }
        }
    }

    private static byte[] CalculateHash(CatalogAdminHandle admin, SafeFileHandle file)
    {
        uint size = 0;
        if (!PInvoke.CryptCATAdminCalcHashFromFileHandle2(admin.DangerousGetHandle(), file, ref size, default)
            && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var hash = new byte[size];
        return PInvoke.CryptCATAdminCalcHashFromFileHandle2(admin.DangerousGetHandle(), file, ref size, hash)
            ? hash[..(int)size]
            : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static unsafe WINTRUST_DATA NewTrustData() => new()
    {
        cbStruct = (uint)sizeof(WINTRUST_DATA),
        dwUIChoice = WINTRUST_DATA_UICHOICE.WTD_UI_NONE,
        fdwRevocationChecks = WINTRUST_DATA_REVOCATION_CHECKS.WTD_REVOKE_NONE,
        dwProvFlags = WINTRUST_DATA_PROVIDER_FLAGS.WTD_CACHE_ONLY_URL_RETRIEVAL | WINTRUST_DATA_PROVIDER_FLAGS.WTD_REVOCATION_CHECK_NONE,
    };

    /// <summary>VERIFY, inspect the signer chain while the provider state is alive, then always CLOSE.</summary>
    private static unsafe int Run(WINTRUST_DATA* data, out bool microsoft)
    {
        var action = PInvoke.WINTRUST_ACTION_GENERIC_VERIFY_V2;
        var noUi = new HWND(-1);
        data->dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_VERIFY;
        var status = PInvoke.WinVerifyTrust(noUi, ref action, data);
        try
        {
            microsoft = status == 0 && IsMicrosoftChain(data->hWVTStateData);
            return status;
        }
        finally
        {
            data->dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_CLOSE;
            PInvoke.WinVerifyTrust(noUi, ref action, data);
        }
    }

    private static unsafe bool IsMicrosoftChain(HANDLE state)
    {
        var provider = PInvoke.WTHelperProvDataFromStateData(state);
        if (provider is null || provider->csSigners == 0 || provider->pasSigners is null)
        {
            return false;
        }

        var signer = provider->pasSigners;
        if (signer->csCertChain == 0 || signer->pasCertChain is null)
        {
            return false;
        }

        var chain = signer->pasCertChain;
        var root = chain[signer->csCertChain - 1];
        if (!root.fTrustedRoot || SimpleName(root.pCert).StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) is false)
        {
            return false;
        }

        var leaf = SimpleName(chain[0].pCert);
        return leaf.Contains("Microsoft Windows", StringComparison.OrdinalIgnoreCase)
            || leaf.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe string SimpleName(CERT_CONTEXT* certificate)
    {
        if (certificate is null)
        {
            return string.Empty;
        }

        Span<char> name = stackalloc char[256];
        var length = PInvoke.CertGetNameString(*certificate, CertNameSimpleDisplayType, 0, null, name);
        return length > 1 ? new string(name[..((int)length - 1)]) : string.Empty;
    }

    private readonly record struct CacheEntry(DateTime LastWriteUtc, long Length, bool Result)
    {
        public bool SameFileAs(CacheEntry other) => LastWriteUtc == other.LastWriteUtc && Length == other.Length;
    }

    private sealed class CatalogAdminHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CatalogAdminHandle(nint admin)
            : base(ownsHandle: true) => SetHandle(admin);

        protected override bool ReleaseHandle() => PInvoke.CryptCATAdminReleaseContext(handle, 0);
    }
}

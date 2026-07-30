using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ParseTiger.Generation;

public interface IProviderSettingsStore
{
    bool HasApiKey(GeneratorProvider provider);
    string? GetApiKey(GeneratorProvider provider);
    ProviderCredentialSource GetApiKeySource(GeneratorProvider provider);
    bool IsApiKeyVerified(GeneratorProvider provider);
    void MarkApiKeyVerified(GeneratorProvider provider);
    void SaveApiKey(GeneratorProvider provider, string apiKey);
    void DeleteApiKey(GeneratorProvider provider);
    string GetModel(GeneratorProvider provider);
    void SaveModel(GeneratorProvider provider, string model);
}

public enum ProviderCredentialSource
{
    None,
    WindowsCredentialManager,
    EnvironmentVariable
}

/// <summary>
/// Stores API keys in Windows Credential Manager for the current user. Non-secret
/// model preferences are stored under HKCU, never in the repository.
/// </summary>
public sealed class WindowsProviderSettingsStore : IProviderSettingsStore
{
    private const string RegistryPath = @"Software\ParseTiger\Providers";
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private readonly string _credentialNamespace;
    private readonly bool _useEnvironmentFallback;

    public WindowsProviderSettingsStore(
        string credentialNamespace = "ParseTiger",
        bool useEnvironmentFallback = true)
    {
        _credentialNamespace = string.IsNullOrWhiteSpace(credentialNamespace)
            ? throw new ArgumentException(
                "A credential namespace is required.",
                nameof(credentialNamespace))
            : credentialNamespace.Trim().TrimEnd('/');
        _useEnvironmentFallback = useEnvironmentFallback;
    }

    public bool HasApiKey(GeneratorProvider provider) =>
        !string.IsNullOrWhiteSpace(GetApiKey(provider));

    public ProviderCredentialSource GetApiKeySource(GeneratorProvider provider)
    {
        if (CredRead(
            CredentialTarget(provider),
            CredentialTypeGeneric,
            0,
            out IntPtr pointer))
        {
            CredFree(pointer);
            return ProviderCredentialSource.WindowsCredentialManager;
        }

        int error = Marshal.GetLastWin32Error();
        if (error != 1168)
        {
            throw new Win32Exception(error, "Could not read the saved API key.");
        }

        return string.IsNullOrWhiteSpace(GetEnvironmentApiKey(provider))
            ? ProviderCredentialSource.None
            : ProviderCredentialSource.EnvironmentVariable;
    }

    public string? GetApiKey(GeneratorProvider provider)
    {
        if (!CredRead(CredentialTarget(provider), CredentialTypeGeneric, 0, out IntPtr pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1168)
            {
                return _useEnvironmentFallback
                    ? GetEnvironmentApiKey(provider)
                    : null;
            }

            throw new Win32Exception(error, "Could not read the saved API key.");
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            return Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / sizeof(char)));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public bool IsApiKeyVerified(GeneratorProvider provider)
    {
        string? apiKey = GetApiKey(provider);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        string? savedFingerprint =
            key?.GetValue(VerificationValueName(provider)) as string;
        return string.Equals(
            savedFingerprint,
            CreateKeyFingerprint(apiKey),
            StringComparison.Ordinal);
    }

    public void MarkApiKeyVerified(GeneratorProvider provider)
    {
        string? apiKey = GetApiKey(provider);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "An API key must be configured before it can be marked verified.");
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue(
            VerificationValueName(provider),
            CreateKeyFingerprint(apiKey),
            RegistryValueKind.String);
    }

    private static string? GetEnvironmentApiKey(GeneratorProvider provider)
    {
        string? value = provider switch
        {
            GeneratorProvider.Gemini =>
                Environment.GetEnvironmentVariable("GEMINI_API_KEY") ??
                Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
            GeneratorProvider.OpenAI =>
                Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            _ => null
        };
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public void SaveApiKey(GeneratorProvider provider, string apiKey)
    {
        if (provider is not (GeneratorProvider.Gemini or GeneratorProvider.OpenAI))
        {
            throw new InvalidOperationException("The selected provider does not use an API key.");
        }

        string value = apiKey?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            throw new ArgumentException("Enter an API key before saving.", nameof(apiKey));
        }

        byte[] bytes = Encoding.Unicode.GetBytes(value);
        IntPtr blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = CredentialTarget(provider),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not save the API key in Windows Credential Manager.");
            }

            ClearVerification(provider);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (blob != IntPtr.Zero)
            {
                byte[] zeros = new byte[bytes.Length];
                Marshal.Copy(zeros, 0, blob, zeros.Length);
            }
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void DeleteApiKey(GeneratorProvider provider)
    {
        if (!CredDelete(CredentialTarget(provider), CredentialTypeGeneric, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1168)
            {
                throw new Win32Exception(error, "Could not remove the saved API key.");
            }
        }

        ClearVerification(provider);
    }

    public string GetModel(GeneratorProvider provider)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        string? saved = key?.GetValue(ModelValueName(provider)) as string;
        return string.IsNullOrWhiteSpace(saved) ? DefaultModel(provider) : saved;
    }

    public void SaveModel(GeneratorProvider provider, string model)
    {
        string value = model?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            throw new ArgumentException("Enter a model name before saving.", nameof(model));
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue(ModelValueName(provider), value, RegistryValueKind.String);
    }

    public static string DefaultModel(GeneratorProvider provider) => provider switch
    {
        GeneratorProvider.Gemini => "gemini-3.5-flash-lite",
        GeneratorProvider.OpenAI => "gpt-5.6-luna",
        _ => string.Empty
    };

    private string CredentialTarget(GeneratorProvider provider) =>
        $"{_credentialNamespace}/{provider}/ApiKey";

    private static string ModelValueName(GeneratorProvider provider) =>
        $"{provider}Model";

    private static string VerificationValueName(GeneratorProvider provider) =>
        $"{provider}VerifiedKeyFingerprint";

    private static string CreateKeyFingerprint(string apiKey)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(apiKey);
        try
        {
            return Convert.ToHexString(SHA256.HashData(keyBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static void ClearVerification(GeneratorProvider provider)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            RegistryPath,
            writable: true);
        key?.DeleteValue(VerificationValueName(provider), throwOnMissingValue: false);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

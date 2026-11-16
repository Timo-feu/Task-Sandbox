using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TaskSandbox.Configuration;

/// <summary>Windows Credential Manager-backed secret storage; no external packages required.</summary>
public sealed class WindowsCredentialSecretStore : IModelSecretStore
{
    private const uint GenericCredential = 1;
    private const uint LocalMachinePersistence = 2;
    private const int NotFoundError = 1168;
    private const int MaxCredentialBytes = 2560;

    public string? Get(string target)
    {
        EnsureWindows();
        ValidateTarget(target);

        if (!CredRead(target, GenericCredential, 0, out IntPtr credentialPointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFoundError)
                return null;
            throw new Win32Exception(error, "Не удалось прочитать секрет из Windows Credential Manager.");
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0)
                return string.Empty;
            if (credential.CredentialBlobSize > MaxCredentialBytes)
                throw new InvalidDataException("Размер сохранённого секрета превышает допустимый предел.");

            int length = checked((int)credential.CredentialBlobSize);
            byte[] bytes = new byte[length];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, length);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Set(string target, string secret)
    {
        EnsureWindows();
        ValidateTarget(target);
        ArgumentNullException.ThrowIfNull(secret);

        byte[] bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length is 0 or > MaxCredentialBytes)
            throw new ArgumentOutOfRangeException(nameof(secret), "Секрет должен занимать от 1 до 2560 байт в UTF-8.");

        IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = LocalMachinePersistence,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось сохранить секрет в Windows Credential Manager.");
        }
        finally
        {
            try
            {
                Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(blob);
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    public void Delete(string target)
    {
        EnsureWindows();
        ValidateTarget(target);

        if (CredDelete(target, GenericCredential, 0))
            return;

        int error = Marshal.GetLastWin32Error();
        if (error != NotFoundError)
            throw new Win32Exception(error, "Не удалось удалить секрет из Windows Credential Manager.");
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Хранилище секретов LocalCodingAgent сейчас использует Windows Credential Manager.");
    }

    private static void ValidateTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 32767)
            throw new ArgumentException("Некорректный идентификатор секрета.", nameof(target));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}

// CredentialStore — persist OIDC refresh token in Windows Credential
// Manager (Generic Credential type), not in plaintext JSON.
//
// Uses CredWrite/CredRead from advapi32; the OS encrypts the secret
// per-user. Refresh token rotation: replace on every successful refresh.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BinaVibe.Auth
{
    public static class CredentialStore
    {
        private const string TargetName = "BinaVibe.RefreshToken";

        // ── CredWrite / CredRead / CredDelete P/Invoke ───────────────────

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CredFree([In] IntPtr buffer);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        public static void SaveRefreshToken(string username, string refreshToken)
        {
            var bytes = Encoding.Unicode.GetBytes(refreshToken);
            var blob = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            try
            {
                var cred = new CREDENTIAL
                {
                    Type = 1,                  // CRED_TYPE_GENERIC
                    TargetName = TargetName,
                    UserName = username,
                    CredentialBlob = blob,
                    CredentialBlobSize = (uint)bytes.Length,
                    Persist = 2,               // CRED_PERSIST_LOCAL_MACHINE
                };
                if (!CredWrite(ref cred, 0))
                    throw new InvalidOperationException($"CredWrite failed: {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                Marshal.ZeroFreeCoTaskMemUnicode(blob);
            }
        }

        public static (string username, string refreshToken)? LoadRefreshToken()
        {
            if (!CredRead(TargetName, 1, 0, out var ptr)) return null;
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                return (cred.UserName, Encoding.Unicode.GetString(bytes));
            }
            finally
            {
                CredFree(ptr);
            }
        }

        public static void Clear()
        {
            CredDelete(TargetName, 1, 0);
        }
    }
}

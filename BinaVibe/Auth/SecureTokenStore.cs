// SecureTokenStore — persist BINA tokens in the Windows Credential Manager
// (Generic Credential), not in plaintext config.json. Windows encrypts the blob
// under the signed-in user's profile.
//
// Uses CredWrite / CredRead / CredDelete from advapi32 (no NuGet dependency).
// The whole token set is stored as one JSON blob under a single target name so a
// refresh (which rotates both tokens) is one atomic write.

using System;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

namespace BinaVibe.Auth
{
    public static class SecureTokenStore
    {
        private const string TargetName = "BinaVibe.Tokens";

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

        public static void Save(BinaTokenSet tokens)
        {
            string blob = JsonConvert.SerializeObject(tokens);
            byte[] bytes = Encoding.Unicode.GetBytes(blob);
            IntPtr mem = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, mem, bytes.Length);
            try
            {
                var cred = new CREDENTIAL
                {
                    Type = 1,                                    // CRED_TYPE_GENERIC
                    TargetName = TargetName,
                    UserName = tokens.UserId.ToString(),
                    CredentialBlob = mem,
                    CredentialBlobSize = (uint)bytes.Length,
                    Persist = 2,                                 // CRED_PERSIST_LOCAL_MACHINE
                };
                if (!CredWrite(ref cred, 0))
                    throw new InvalidOperationException($"CredWrite failed: {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                Marshal.ZeroFreeCoTaskMemUnicode(mem);
            }
        }

        public static BinaTokenSet Load()
        {
            if (!CredRead(TargetName, 1, 0, out IntPtr ptr)) return null;
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                if (cred.CredentialBlobSize == 0) return null;
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                string blob = Encoding.Unicode.GetString(bytes);
                return JsonConvert.DeserializeObject<BinaTokenSet>(blob);
            }
            catch
            {
                return null;
            }
            finally
            {
                CredFree(ptr);
            }
        }

        public static void Clear()
        {
            try { CredDelete(TargetName, 1, 0); } catch { /* not present is fine */ }
        }
    }
}

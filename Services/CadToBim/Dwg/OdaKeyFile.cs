// AI-647: parse the ODA licence file (oda.key). The values only ever live in the returned
// tuple, which goes straight to odActivate; Shape is the only thing that may be logged.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Services.CadToBim.Dwg
{
    public static class OdaKeyFile
    {
        /// <summary>
        /// Line 1 is base64 of <c>"&lt;userInfo&gt;","&lt;userSignature&gt;"</c>; later lines
        /// (a label) are ignored. Fallback: two raw non-empty lines. Throws
        /// InvalidDataException (no key text in the message) when neither shape fits.
        /// </summary>
        public static (string Info, string Signature, string Shape) Read(string path)
        {
            string[] lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            if (lines.Length == 0) throw new InvalidDataException("the ODA key file is empty");
            try
            {
                MatchCollection q = Regex.Matches(Encoding.UTF8.GetString(Convert.FromBase64String(lines[0])), "\"([^\"]*)\"");
                if (q.Count == 2 && q[0].Groups[1].Length > 0 && q[1].Groups[1].Length > 0)
                    return (q[0].Groups[1].Value, q[1].Groups[1].Value,
                            "base64 pair, lengths " + q[0].Groups[1].Length + "/" + q[1].Groups[1].Length);
            }
            catch (FormatException) { }
            if (lines.Length < 2) throw new InvalidDataException("the ODA key file has an unknown shape");
            return (lines[0], lines[1], "raw lines, lengths " + lines[0].Length + "/" + lines[1].Length);
        }
    }
}

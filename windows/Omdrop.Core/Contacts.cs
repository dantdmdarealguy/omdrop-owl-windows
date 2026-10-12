using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Text.RegularExpressions;

namespace Omdrop;

/// <summary>
/// Contacts Only: lets in senders whose Apple-signed record shows a phone
/// number or email that's in the user's contacts (a .vcf export).
///
/// A sender's SenderRecordData is PKCS#7 signed data around a plist that holds
/// SHA-256 hashes of the identifiers Apple verified for its Apple ID
/// (ValidatedPhoneHashes, ValidatedEmailHashes). The signature is checked (the
/// record wasn't altered; the signer must be an Apple certificate), then the
/// hashes are compared with our contacts' identifiers, hashed the same way.
/// Phone numbers are hashed in each form Apple might have used (E.164 with and
/// without "+", and the bare digits), since that's not documented.
/// </summary>
public static class Contacts
{
    /// A filter for Receiver.AllowSender; with no contacts file, nobody passes.
    public static Func<byte[]?, bool> Allow(string? vcfPath)
    {
        var known = vcfPath != null && File.Exists(vcfPath) ? LoadHashes(vcfPath) : new HashSet<string>();
        Log.Info($"contacts only: {known.Count / 3} identifier(s) from {vcfPath ?? "(no contacts file)"}");
        return record =>
        {
            if (record == null)
                return false;
            var hashes = RecordHashes(record);
            if (hashes == null)
                return false;
            bool match = hashes.Any(known.Contains);
            Log.Debug($"contacts only: sender record with {hashes.Count} hash(es), {(match ? "a contact" : "not a contact")}");
            return match;
        };
    }

    /// The validated identifier hashes in an Apple-signed sender record, or null
    /// if it isn't one.
    public static List<string>? RecordHashes(byte[] record)
    {
        try
        {
            var cms = new SignedCms();
            cms.Decode(record);
            cms.CheckSignature(verifySignatureOnly: true);
            var signer = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0].Certificate : null;
            if (signer == null || !signer.Issuer.Contains("Apple", StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug($"contacts only: record not signed by Apple ({signer?.Issuer})");
                return null;
            }
            if (Plist.Read(cms.ContentInfo.Content) is not Dictionary<string, object?> plist)
                return null;
            var hashes = new List<string>();
            foreach (var key in new[] { "ValidatedPhoneHashes", "ValidatedEmailHashes" })
                foreach (var h in plist.GetValueOrDefault(key) as List<object?> ?? [])
                    hashes.Add(h switch
                    {
                        byte[] b => Convert.ToHexString(b).ToLowerInvariant(),
                        string s => s.ToLowerInvariant(),
                        _ => "",
                    });
            return hashes;
        }
        catch (Exception e) when (e is CryptographicException or FormatException or InvalidOperationException)
        {
            Log.Debug($"contacts only: unreadable sender record ({e.Message})");
            return null;
        }
    }

    static HashSet<string> LoadHashes(string vcf)
    {
        var hashes = new HashSet<string>();
        foreach (var raw in File.ReadAllLines(vcf))
        {
            var line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var key = line[..colon].ToUpperInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key.StartsWith("EMAIL"))
            {
                hashes.Add(Sha256(value.ToLowerInvariant()));
            }
            else if (key.StartsWith("TEL"))
            {
                var digits = Regex.Replace(value, @"[^\d+]", "");
                var bare = digits.TrimStart('+');
                if (bare.Length == 10 && !digits.StartsWith('+'))
                    bare = "1" + bare; // a North American number without its country code
                hashes.Add(Sha256("+" + bare));
                hashes.Add(Sha256(bare));
                hashes.Add(Sha256(digits));
            }
        }
        return hashes;
    }

    static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

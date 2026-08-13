namespace SoftwareLicensing;

public static class TrustedPublicKeys
{
    // Add future public keys before issuing licences with their key IDs.
    // Keep old public keys so perpetual licences continue to validate.
    public static readonly IReadOnlyDictionary<string, string> ByKeyId =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["primary-2026"] =
                """
                -----BEGIN PUBLIC KEY-----
                MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEyhRLsZBPHjGwCg9scoztdyAC3IDc
                wjnytX6fjU/u44Mvm57xrlyJwXvJWZEfFMVSVjrZo7bpnq2hbZ8prBIqDA==
                -----END PUBLIC KEY-----
                """,

            ["secondary-2026"] =
                """
                -----BEGIN PUBLIC KEY-----
                MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvlmYPNBCgU50AIVzrXNpPcUU7PrP
                /93veInk5aHL41yk0MIvBoGEvusKhl5sZMjkxFX2pnJm9lVYvZxc5s0UvA==
                -----END PUBLIC KEY-----
                """
        };
}

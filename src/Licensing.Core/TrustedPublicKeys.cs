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
                MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6LRnpzOZyedrb1xwkrzklCtpwPZw
                sF1JtPAAblmWcqfwLcAsEYXgaJbMdvkkynm8ap06K5XD45glll0S6Satkw==
                -----END PUBLIC KEY-----
                """,

            ["secondary-2026"] =
                """
                -----BEGIN PUBLIC KEY-----
                MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjtzhfUytpgvZQocYHeWPGLTvYsrg
                m69//572655IypI2BmsGasEMgLTjcNYklarnXh+4SM8i0nWbLEWFF9aDxg==
                -----END PUBLIC KEY-----
                """
        };
}

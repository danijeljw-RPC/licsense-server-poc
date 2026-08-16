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
                MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXXu3cKxsg4XRn3w+DBklf2uL1Zzm
                2ZS9bU3kyD7SY5AYM5fVdBAavnS4esT4dpBU1sV4RLPfVuH2Vu7f7BDXtQ==
                -----END PUBLIC KEY-----
                """,

            ["secondary-2026"] =
                """
                -----BEGIN PUBLIC KEY-----
                MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEqlkXV3jGhEOCaRq6aD11pYmXVNgv
                6rJS/aY9WtJHHH6kaTKFxgj1eu8a7Qw4fVh+wRnFd4T29taRbKDxgmPpaA==
                -----END PUBLIC KEY-----
                """
        };
}

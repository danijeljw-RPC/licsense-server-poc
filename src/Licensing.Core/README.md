# Licensing.Core

Offline verification for signed licence files. Reference this package from any product that needs to check whether a licence is valid — no network call, no server dependency, no public-key handling on your part.

## Install

```sh
dotnet add package Licensing.Core
```

Or reference the DLL/zip/tar.gz directly from a [GitHub release](https://github.com/repasscloud/license-server-app/releases) of the licensing server, verified against the accompanying `SHA256SUMS.txt`.

## Usage

```csharp
using SoftwareLicensing;

try
{
    var verified = LicenseVerifier.VerifyFile(licensePath);

    var entitlement = LicenseVerifier.ValidateProduct(
        verified,
        product: "gcexp",
        releaseDate: new DateOnly(2028, 12, 31));

    // Use entitlement.Edition, entitlement.Seats, and other trusted values.
}
catch (LicenseValidationException ex)
{
    // Do not unlock the product. Log or display ex.Message as appropriate.
}
```

`LicenseVerifier.VerifyFile` parses the licence file, checks its ECDSA signature against the trusted public key matching the licence's `keyId`, and returns a `VerifiedLicense`. It chooses the public key automatically — the application does not receive or request a public-key path.

`LicenseVerifier.ValidateProduct` then checks that the licence actually entitles the given product, that it hasn't expired, and (if a `releaseDate` is supplied) that the licence's update window covers that release.

For licences with device-bound activation, `LicenseVerifier.ValidateActivation` (called automatically by `ValidateProduct`) checks the licence is activated for the current device and that any activation lease hasn't expired.

## Trust model

The public keys trusted for verification are compiled into this package (`TrustedPublicKeys`) — not fetched at runtime, not read from a config file. This means:

- **Fully offline**: verification never requires network access, and there is no server response to spoof or intercept.
- **Key rotation requires a new package version**: when the signing key used to issue licences rotates, this package must be rebuilt with the new public key and republished.| Products built against an older version of `Licensing.Core` will not trust licences signed with a key added after that version was built — update your `Licensing.Core` reference before relying on licences signed with a new key.
- Old public keys are kept in `TrustedPublicKeys` indefinitely so previously issued perpetual licences keep verifying even after rotation.

## License

Apache-2.0. See [LICENSE](https://github.com/repasscloud/license-server-app/blob/main/LICENSE).

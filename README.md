# Software licensing POC

This repository is a proof-of-concept foundation for issuing one signed licence that can cover multiple commercial products. It has four projects:

- `Licensing.Core` is the shared licence contract, schema validation, and canonical JSON implementation.
- `LicenseGenerator` generates ECDSA P-256 keys and signs schema-valid licence data.
- `LicenseValidator` verifies signatures using public keys compiled into the application, validates the schema, and enforces product rules.
- `LicenseServer` is a dependency-free ASP.NET Core mock service for activation, leases, deactivation, transfer, and revocation.

The signer and validator deliberately share `Licensing.Core`, so their interpretation of a licence cannot drift independently.

## Recommended device and transfer model

There is no C# value that proves *absolutely* that untrusted software is running on one physical computer. A machine owner can patch the client, clone a VM, or spoof ordinary hardware identifiers. MAC addresses, CPU IDs, disk serials, host names, and ad-hoc combinations of them are especially poor primary identities: they can be missing, replaced, duplicated, virtualised, or changed by normal repairs.

Use these layers instead:

1. Give each installation a stable, privacy-preserving device ID. This PoC hashes an OS installation ID with a product namespace in [`DeviceIdentity.cs`](src/Licensing.Core/DeviceIdentity.cs). On Windows it hashes `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`; on Linux it hashes `/etc/machine-id`. Only the hash is sent to the server or placed in the signed licence.
2. Put that device ID in the signed `deviceBinding` object. A copied licence then fails validation on another installation.
3. Make the service authoritative for activation state. It permits one active device, rejects a second device, and releases the licence only after authenticated deactivation.
4. For a production Windows product, replace the PoC identifier with a per-device non-exportable key held by the TPM and make online validation a challenge/response proof of key possession. The [.NET CNG provider API](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cngprovider?view=net-10.0) exposes the Microsoft Platform Crypto Provider, and Microsoft documents that provider as TPM-backed in [CNG Key Storage Providers](https://learn.microsoft.com/en-us/windows/win32/seccertenroll/cng-key-storage-providers). Another Windows option is publisher-scoped [`SystemIdentification.GetSystemIdForPublisher`](https://learn.microsoft.com/en-us/uwp/api/windows.system.profile.systemidentification.getsystemidforpublisher), which prefers TPM, then UEFI, then a registry fallback and reports which source it used.

Windows also defines SMBIOS-based Computer Hardware IDs, but Microsoft describes them as hashes of combinations of SMBIOS fields and notes that a value exists only when all fields in that combination are populated. They are useful signals, not a secret or proof of possession. See [Computer Hardware IDs](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/computer-hardware-ids).

Display the identifier used by this PoC:

```powershell
dotnet run --project src/LicenseValidator -- --device-id
```

The signed server-issued payload adds:

```json
{
  "deviceBinding": {
    "scheme": "os-machine-id-sha256-v1",
    "deviceId": "64-HEX-CHARACTERS...",
    "deviceName": "ACCOUNTS-PC"
  },
  "activation": {
    "activationId": "b780692f-1d21-4218-b973-2f87a0daf016",
    "mode": "online",
    "activatedAt": "2026-08-12T08:00:00Z",
    "refreshAfter": "2026-08-13T08:00:00Z",
    "leaseExpiresAt": "2026-08-19T08:00:00Z"
  }
}
```

`LicenseVerifier.ValidateActivation` compares the signed ID with the current machine using a fixed-time comparison and enforces the signed lease cutoff. `ValidateProduct` calls it automatically, so normal product validation cannot accidentally skip device binding.

### Transfer and invalidation rules

The intended state transition is:

```text
available -> active on device A -> deactivated -> active on device B
                    |
                    +-> revoked (terminal)
```

- Activation requires the customer activation code plus a random client-generated activation token. The service stores SHA-256 hashes of both, not their plaintext values.
- A retry with the same request ID, device, and token is idempotent. A different activation receives HTTP `409 Conflict` while a device is active.
- Deactivation requires the activation ID, device ID, and activation token. After it succeeds, another machine may activate.
- Revocation is server state. Online validation and refresh reject a revoked licence immediately.
- Online clients receive a seven-day signed lease and should refresh after one day. If they stay disconnected, the local lease eventually expires, bounding how long a revocation can be ignored.
- A perpetual offline file cannot learn that it was revoked. The service can refuse a transfer until an offline deactivation receipt is imported, but it cannot prove that a customer deleted every copy. Strong enforcement requires periodic connectivity, a short lease, or external hardware such as a dongle. This is a fundamental boundary, not a C# implementation detail.

For a real product, offer separate policies rather than claiming identical guarantees: `online` (short renewable lease and prompt revocation), `offline` (long or no lease with weaker revocation), and possibly a customer-specific air-gapped lease duration.

## Mock activation API

Run the local service:

```powershell
dotnet run --project src/LicenseServer -- --urls http://127.0.0.1:5187
```

That command is for manually exercising the API. `Test-ActivationFlow.ps1` is self-contained: it builds the solution, starts a separate temporary server on a random local port, runs the flow, stops that server, and removes its temporary files. Stop the manually started server with `Ctrl+C` when finished; it does not need to be running for the test.

The seeded PoC licence is `LIC-POC-0001`; its activation code is `POC-DEMO-ACTIVATION-CODE`. The local admin key is `local-poc-admin-key`. These are intentionally public demo credentials and must never become production defaults.

| Endpoint | Purpose |
| --- | --- |
| `POST /api/v1/licenses/{licenseId}/activate` | Online activation or operator-mediated offline issuance. |
| `POST /api/v1/activations/{activationId}/validate` | Check current server state. |
| `POST /api/v1/activations/{activationId}/refresh` | Issue a fresh signed online lease. |
| `POST /api/v1/activations/{activationId}/deactivate` | Authenticate deactivation and make the licence transferable. |
| `GET /api/v1/admin/licenses/{licenseId}` | Inspect state; requires `X-Admin-Key`. |
| `POST /api/v1/admin/licenses/{licenseId}/revoke` | Permanently revoke; requires `X-Admin-Key`. |

This service deliberately uses an in-memory store so the PoC has no package or database dependency. Its locking and state transitions are realistic, but a production service must replace it with transactional durable storage, unique constraints, an audit log, customer/operator authentication, rate limiting, secret management, and a KMS/HSM signing boundary. The web process loads a development PEM only for this PoC.

### Offline issuance

On the offline target machine, create a request file:

```powershell
./scripts/New-OfflineActivationRequest.ps1
```

Move `artifacts/offline-activation-request.json` to an operator-connected machine. The file contains an activation code and bearer token, so transport it securely. The operator posts the unchanged JSON to:

```text
POST /api/v1/licenses/LIC-POC-0001/activate
```

The response contains `signedLicense` as a JSON string; save that string unchanged as a `.license` file and carry it back to the target. Keeping the signed document opaque avoids date/string normalization by intermediary JSON tooling. Signature, product, expiry, and device checks then work with no server connection. Keep the request credentials in OS-protected storage because they are needed to submit a future deactivation. In production, an offline deactivation command should disable local state and create a signed/request-authenticated receipt for the operator to import before transfer. It still cannot force deletion of copied offline files.

## Licence design

A licence contains customer-level identity once and a separate entitlement for every product:

```json
{
  "licenseId": "LIC-4F81CDA2",
  "customer": "Example Pty Ltd",
  "issuedAt": "2026-08-12T06:30:00Z",
  "metadata": {
    "purchaseOrder": "PO-88429",
    "contactEmail": "licensing@example.com",
    "contactPerson": "Jane Smith",
    "addressLine1": "100 Example Street"
  },
  "entitlements": [
    {
      "product": "gcexp",
      "edition": "professional",
      "licenseType": "perpetual",
      "seats": 5,
      "updatesUntil": "2027-08-12"
    },
    {
      "product": "winupd",
      "edition": "professional",
      "licenseType": "subscription",
      "seats": 10,
      "expiresAt": "2028-12-31T23:59:59Z",
      "updatesUntil": "2028-12-31"
    }
  ]
}
```

The signed file wraps this data with the format, algorithm, signing key ID, and signature:

```json
{
  "format": "software-license-v1",
  "algorithm": "ECDSA-P256-SHA256",
  "keyId": "primary-2026",
  "license": { },
  "signature": "..."
}
```

### How an application chooses the correct public key

The `.license` file contains the signed key identifier, such as:

```json
"keyId": "primary-2026"
```

It intentionally does **not** contain the public key. Each application ships `Licensing.Core`, which contains the trusted public-key dictionary in [`src/Licensing.Core/TrustedPublicKeys.cs`](src/Licensing.Core/TrustedPublicKeys.cs):

```text
signed keyId "primary-2026"
        ↓
TrustedPublicKeys.ByKeyId["primary-2026"]
        ↓
embedded primary public PEM
        ↓
verify the signature over format + algorithm + keyId + licence data
```

The `keyId` is itself covered by the signature. Changing it invalidates the signature. An unknown ID is rejected. A public key is never accepted from the licence file, command line, or customer, because accepting it would allow an attacker to provide their own key and signature.

Public keys do not update themselves through licence files. To add a signing key, add its public PEM to `TrustedPublicKeys.cs`, rebuild/release each product, and only then issue licences with the new key ID. Keep old public keys in later product releases while old licences must remain valid.

### Calling validation directly from an application

A product should reference `Licensing.Core` and call the same verifier used by the CLI:

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

`LicenseVerifier.VerifyFile` chooses the embedded public key automatically. The application does not receive or request a public-key path.

### Licence-level fields

| Field | Required | Meaning |
| --- | --- | --- |
| `licenseId` | Yes | Globally unique, immutable licence identity. The production server should enforce uniqueness. |
| `customer` | Yes | Display/legal customer name. Use a separate internal customer ID in the production database. |
| `issuedAt` | Yes | Exact signed issuance instant, including timezone. UTC with `Z` is recommended. |
| `metadata` | No | Customer-selected custom scalar fields in a controlled namespace. |
| `deviceBinding` | No | Signed device scheme, hashed device ID, and optional display name. Must appear with `activation`. |
| `activation` | No | Signed activation identity/mode and optional online lease times. Must appear with `deviceBinding`. |
| `entitlements` | Yes | Non-empty collection with no duplicate product names. |

### Product entitlement fields

| Field | Required | Meaning |
| --- | --- | --- |
| `product` | Yes | Stable product code used by the application, such as `gcexp`. Do not use a display name. |
| `edition` | Yes | Product-specific feature tier, such as `standard`, `professional`, or `enterprise`. |
| `licenseType` | Yes | Commercial model, such as `perpetual`, `subscription`, or `trial`. |
| `seats` | Yes | Positive integer capacity for this product. A later server must define what consumes a seat. |
| `expiresAt` | No | Exact UTC runtime cutoff. At or after this instant, that product is invalid. Missing means no runtime expiry. |
| `updatesUntil` | No | Last covered product release date, inclusive. Missing means update eligibility is not date-limited. |

No other fields are allowed at licence or entitlement level. This prevents misspellings from being silently signed and protects the contract from accidental collisions with future standard fields.

### Custom metadata

Customer-selected fields must be placed directly inside the optional `metadata` object:

```json
"metadata": {
  "purchaseOrder": "PO-88429",
  "contactEmail": "licensing@example.com",
  "contactPerson": "Jane Smith",
  "addressLine1": "100 Example Street",
  "priorityCustomer": true,
  "accountNumber": 4812
}
```

Metadata names must use lower camel case: the first character is `a`–`z`, followed only by ASCII letters or digits. Examples such as `purchaseOrder`, `contactEmail`, and `addressLine1` are valid; `PurchaseOrder`, `purchase_order`, spaces, dots, and hyphens are rejected.

Metadata values may be strings, numbers, or booleans. Null, nested objects, and arrays are rejected so the custom-data contract remains flat and predictable. Metadata is covered by the licence signature, but it is visible plaintext—not encrypted—so avoid secrets and unnecessary personal information.

`expiresAt` and `updatesUntil` are intentionally different. A perpetual licence can keep running after `updatesUntil`, but cannot use a product release dated after it. A subscription can use `expiresAt` to stop runtime access entirely.

## Product integration rules

Every product build should contain two immutable values:

1. Its stable product code, for example `gcexp`.
2. Its release date in `yyyy-MM-dd`, generated by the release pipeline rather than taken from the user's clock.

At startup or before unlocking licensed functionality, the product should validate both:

```powershell
dotnet run `
    --project src/LicenseValidator `
    -- `
    --license licenses/customer.license `
    --product gcexp `
    --release-date 2027-04-18
```

Validation succeeds only when:

- the envelope signature is valid;
- the `keyId` maps to a trusted embedded public key;
- the required licence schema is valid;
- the requested product exists;
- the product has not reached `expiresAt`; and
- the build release date is not after `updatesUntil`.

The `updatesUntil` date is inclusive. An entitlement through `2027-08-12` covers a release dated `2027-08-12`, but not `2027-08-13`.

## Build and test

The solution targets .NET 10:

```powershell
dotnet build SoftwareLicensing.slnx --configuration Release
./scripts/Test-LicenseFlow.ps1
./scripts/Test-ActivationFlow.ps1
```

The first test signs with both trusted keys and covers required fields, strict metadata placement and naming, nested metadata reads, case-insensitive reads, per-product editions/types/seats, active and expired products, update cutoffs, malformed dates, tampering, unknown keys, and public-key substitution. The activation test starts the local API and proves correct-device validation, blocked transfer, authenticated deactivation, successful transfer, wrong-device rejection, offline issuance, and the online/offline revocation boundary.

Use `-KeepArtifacts` to retain its temporary licences:

```powershell
./scripts/Test-LicenseFlow.ps1 -KeepArtifacts
```

## Generate and validate both key demos

Two visible demo inputs are included:

- [`input/demo-primary-license-data.json`](input/demo-primary-license-data.json)
- [`input/demo-secondary-license-data.json`](input/demo-secondary-license-data.json)

Run the demo script:

```powershell
./scripts/New-DemoLicenses.ps1
```

It performs the complete flow:

1. Signs `demo-primary.license` with the primary private development key and `keyId: primary-2026`.
2. Validates it using the embedded primary public key without a `--public-key` argument.
3. Signs `demo-secondary.license` with the secondary private development key and `keyId: secondary-2026`.
4. Validates it using the embedded secondary public key.

The larger `Test-LicenseFlow.ps1` also proves that a private key/key ID mismatch is rejected, caller-supplied public keys are rejected, unknown key IDs fail, and tampered signed data fails.

## Keys and trust

The validator initially trusts:

| Key ID | Intended use | Public-key file |
| --- | --- | --- |
| `primary-2026` | Normal production issuance | `keys/license-primary-2026-public.pem` |
| `secondary-2026` | Backup/manual issuance | `keys/license-secondary-2026-public.pem` |

Both complete public PEM values are already compiled into `TrustedPublicKeys.cs`. The PEM files in `keys/` are convenient development copies; validation uses the source-code values, not those files.

Generate a pair with unused output paths:

```powershell
dotnet run `
    --project src/LicenseGenerator `
    -- `
    keygen `
    --private-key keys/license-primary-2028-private.pem `
    --public-key keys/license-primary-2028-public.pem
```

Key generation refuses to overwrite existing files by default. `--force` exists for deliberate replacement of an unused development key, but replacing a key already used for issuance will invalidate its licences.

Add only the public PEM to [`src/Licensing.Core/TrustedPublicKeys.cs`](src/Licensing.Core/TrustedPublicKeys.cs), mapped to its exact key ID. Release applications containing the new public key before issuing with it. Keep older public keys while their licences must validate. Never distribute private keys or accept a public key supplied beside the licence.

The generator also uses this trust map to verify that the selected private key matches `--key-id`. This prevents an operator from issuing an unusable licence with the correct ID but the wrong private key.

Production private keys should be held in a managed KMS/HSM or isolated signing service. The future API should ask that service to sign; it should not load PEM private keys into a public web process.

## Sign a licence

Edit [`input/license-data.json`](input/license-data.json), then run:

```powershell
dotnet run `
    --project src/LicenseGenerator `
    -- `
    sign `
    --input input/license-data.json `
    --output licenses/customer.license `
    --private-key keys/license-primary-2026-private.pem `
    --key-id primary-2026
```

The generator rejects incomplete or ambiguous data rather than signing it. Required strings cannot be blank; seats must be positive integers; products cannot be duplicated case-insensitively; instants require a timezone; update dates must be exact `yyyy-MM-dd` values; and custom fields must follow the metadata rules above.

## Inspect signed data

All reads verify the signature and schema first. Field and product names are case-insensitive:

```powershell
# Licence-level data
dotnet run --project src/LicenseValidator -- --license licenses/customer.license --field licenseId
dotnet run --project src/LicenseValidator -- --license licenses/customer.license --field customer
dotnet run --project src/LicenseValidator -- --license licenses/customer.license --field issuedAt

# Customer-selected metadata
dotnet run --project src/LicenseValidator -- --license licenses/customer.license --field metadata.purchaseOrder
dotnet run --project src/LicenseValidator -- --license licenses/customer.license --field metadata.contactEmail

# Product-level data (also enforces expiresAt)
dotnet run --project src/LicenseValidator -- --license licenses/customer.license --product gcexp --field edition
dotnet run --project src/LicenseValidator -- --license licenses/customer.license --product gcexp --field licenseType
dotnet run --project src/LicenseValidator -- --license licenses/customer.license --product gcexp --field seats
dotnet run --project src/LicenseValidator -- --license licenses/customer.license --product gcexp --field updatesUntil
```

Exit codes are `0` for success, `1` for an invalid/expired/not-covered licence request, and `2` for arguments or runtime errors.

## Production-server evolution

This format is a good offline signed entitlement, but a commercial licensing service needs additional controls:

- Persist customers, products, entitlements, licences, signing-key metadata, issuance events, and revocations in a transactional database.
- Generate `licenseId` server-side and make issuance idempotent so retries cannot create accidental duplicates.
- Keep an immutable audit trail of who issued, changed, renewed, revoked, and downloaded a licence.
- Define controlled vocabularies for product codes, editions, and licence types in server data rather than trusting arbitrary operator input.
- Model renewals and upgrades as new signed licence versions. Never mutate an already issued signed file.
- Publish signed revocation lists or require periodic online lease renewal where prompt revocation matters.
- Use short-lived signed leases or signed server-time responses for subscriptions. This offline POC uses the client UTC clock, which a machine owner may roll backwards.
- Define seat semantics explicitly: named user, activated device, concurrent process, or concurrent server lease. A signed seat number alone cannot enforce concurrency.
- Decide whether licences are transferable and whether optional organisation, device, or deployment binding is needed.
- Rate-limit issuance/download endpoints, authenticate operators and customers, and separate signing authority from normal administration.
- Back up signing keys and document rotation, compromise, disaster recovery, and key-revocation procedures before production issuance.

ECDSA signatures provide authenticity and tamper detection, not secrecy. Anyone holding a licence file can read its JSON, so do not put secrets or unnecessary personal data in it.

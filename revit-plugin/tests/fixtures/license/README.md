# License test fixtures (TEST KEY ONLY)

Signed with a throwaway TEST keypair whose private half is intentionally public knowledge —
these can never validate against the production key embedded in the add-in. valid.apexlic
(expires 2030), expired.apexlic (expired 2025), tampered.apexlic (one payload byte altered,
signature intact → must fail signature verification).

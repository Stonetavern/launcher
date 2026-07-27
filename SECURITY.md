# Security Policy

## Reporting

Report suspected vulnerabilities privately to `innkeeper@stonetavern.app`.
Do not open a public issue for anything that could put players at risk.

Please include what you did, what happened, and what you expected. A proof of
concept helps but is not required.

## Scope

In scope: this launcher, its update mechanism, its download and verification
path, and the release artifacts published from this repository.

Out of scope: the game servers themselves, the website, and anything reachable
only from inside the operator network.

## What we care about most

- Anything that lets an attacker make the launcher execute or install content
  the operator did not publish
- Anything that weakens or bypasses checksum or signature verification
- Anything that exposes player credentials or session material

## Verifying a release

Every release carries `SHA256SUMS` and a signed manifest. The public signing key
is in `deploy/assets/stonetavern-release-signing.pub.asc`. Verify before running
a build you did not compile yourself.

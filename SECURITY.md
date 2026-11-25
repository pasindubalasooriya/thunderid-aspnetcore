# Security policy

## Supported versions

| Version | Security fixes |
|---|---|
| Latest 0.x release (0.4.x today) | Yes, until 1.0.0 |
| Earlier 0.x releases | No: upgrade to the latest |

From 1.0.0, security fixes go to the latest 1.x minor release.

## Reporting a vulnerability

Report it privately through GitHub:
[Report a vulnerability](https://github.com/pasindubalasooriya/thunderid-aspnetcore/security/advisories/new).
Don't open a public issue or pull request for it.

Include what you can: the affected version, how to reproduce it, and what an attacker could do with
it. Never include real tokens, client secrets or passwords.

## What happens next

- I acknowledge the report within 7 days.
- I confirm or rule out the issue, and tell you which, within 14 days.
- When it's confirmed, I fix it in a new release and publish a GitHub security advisory, crediting
  you unless you'd rather not be named.

## Out of scope

- **ThunderID itself**, the server: report it to ThunderID, as its
  [security policy](https://github.com/thunder-id/thunderid/blob/main/SECURITY.md) describes.
- **ASP.NET Core and the .NET runtime**: report them to Microsoft, as
  [.NET's security policy](https://github.com/dotnet/aspnetcore/security/policy) describes.
- The credentials in `tests/e2e/thunderid`: they're fixed test-only values for a local container,
  published on purpose.

# Security policy

## Reporting a vulnerability

If you find a security issue in Klip, please report it privately instead of
opening a public issue.

Use GitHub's [private vulnerability reporting](https://github.com/PoBruno/klip/security/advisories/new)
to reach the maintainer directly. Include:

- what the issue is and where it lives in the code,
- steps to reproduce it,
- the impact you think it has.

You can expect a first reply within a few days. Once the issue is confirmed and
fixed, the fix ships in the next release and the report gets credited (unless you
prefer to stay anonymous).

## Scope

Klip runs fully on your machine and does not talk to any server, so most classic
web/network issues do not apply. Areas that are in scope:

- the clipboard engine and how it stores data on disk,
- the registry takeover of Win+V and Print Screen,
- the global keyboard hook,
- anything that could leak clipboard contents or run code you did not intend.

## Supported versions

Only the latest release gets security fixes.

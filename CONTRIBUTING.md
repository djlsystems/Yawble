# Contributing to Yawble

Thanks for helping. This page covers the agreement you sign, how to build and test, and what a
pull request needs.

## Contributor License Agreement

Every contributor must sign the [Contributor License Agreement](CLA.md) before a pull request
can be merged. The CLA Assistant check asks you to sign it on your first pull request; after
that it recognises you. If you contribute on behalf of a company, the entity section of the
agreement applies as well.

To read and sign it before you open a pull request, sign in with GitHub at
<https://cla-assistant.io/djlsystems/Yawble>. What you sign there is [CLA.md](CLA.md): CLA
Assistant shows it from a [gist](https://gist.github.com/djlsystems/5bcd4668621fa03ae28cc7548d5f0e64)
that is kept identical to that file, and any change to one is made to the other.

You keep the copyright in your contribution. The agreement grants DJL Systems, Inc. and its
successors a license to it, including the right to offer it under other terms. A
`Signed-off-by` line (DCO) is not a substitute for the CLA.

Yawble is free to use, self-host and modify, including at work; the only thing you can't do is
sell Yawble as a hosted service, and every version becomes Apache 2.0 after two years. The terms
are the [Functional Source License, Version 1.1, Apache-2.0 future license](LICENSE)
(FSL-1.1-ALv2). Do not submit code you cannot license under the CLA, and do not submit
proprietary or enterprise code: this repository holds the public core only.

## Build and test

Requirements: .NET 10 SDK, Node 22 with npm, and Go.

| What | Command |
|---|---|
| .NET build | `dotnet build tests/Harness.Tests/Harness.Tests.csproj` |
| .NET tests | `dotnet tests/Harness.Tests/bin/Debug/net10.0/Harness.Tests.dll` |
| .NET tests on Linux in a container (from any OS) | `scripts/test-in-container.sh` |
| Web app | `cd web && npm ci && npm run typecheck && npm test` |
| Operator CLI | `cd cli && go vet ./... && go test ./...` |

Run the test dll directly after a build: `dotnet test` on these Microsoft.Testing.Platform
projects can report zero tests. To run one class while you work, pass
`--filter-class '*ClassName'` to the dll.

## Pull requests

- Keep each pull request to one change, with a title and description that say what it does
  and why.
- Add or update a test for any change in behaviour, and see it fail before your fix makes it
  pass.
- Run the suites your change touches and make sure they are green before you ask for review.
- Read [AGENTS.md](AGENTS.md). It lists the rules the code depends on and the test that pins
  each one; keep those tests green and the rules true.
- Keep the product name out of the code: it lives in `web/src/presentation/product.ts` and
  `web/package.json`, and `BrandLeakTests` fails otherwise.

## Trademarks

Yawble and the Yawble logo are trademarks of DJL Systems, Inc. The license does not grant any
right to use them. A fork or a hosted service built from this code must not use the Yawble name
or logo.

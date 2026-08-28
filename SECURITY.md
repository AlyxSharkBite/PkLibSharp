# Security Policy

## Reporting a vulnerability

Please report security issues **privately**, not through a public issue.

Use GitHub's private vulnerability reporting, which is enabled on this repository:

**https://github.com/AlyxSharkBite/PkLibSharp/security/advisories/new**

That opens a private advisory visible only to you and the maintainer. If you cannot use it, open a
public issue containing no details beyond a request for a private channel, and you will be sent one.

### What to include

A report is much easier to act on with:

- The version of PkLibSharp affected, and the .NET runtime you observed it on
- A compressed or uncompressed input that triggers the behaviour, as a file or a hex dump
- Which API path was used: `Imploder`, `Exploder`, `IPkLibCodec`, or the callback overloads
- What you expected to happen, and what happened instead

### What to expect

| Stage | Target |
| --- | --- |
| Acknowledgement of your report | within 3 business days |
| Initial assessment, with a severity judgement | within 7 days |
| Fix or documented mitigation for a confirmed issue | within 30 days |
| Public advisory, credited to you unless you prefer otherwise | after the fix ships |

This is a personal project maintained in spare time, so those are honest targets rather than a
contractual SLA. If a report goes unanswered past these windows, you are free to disclose publicly.

## Supported versions

| Version | Supported |
| --- | --- |
| 1.0.x | Yes |
| < 1.0 | No |

Fixes land on `main` and ship in the next release. There are no long-term support branches.

## Threat model

PkLibSharp decodes a compressed format that is, in most real deployments, **attacker controlled** —
it is the compression used inside MPQ archives and similar container formats. Assume any stream
handed to `Exploder` may be hostile. The following are in scope for a report:

- Memory corruption, out-of-range access, or any unhandled exception type escaping `Exploder`
  other than `PkLibException`
- Infinite loops or non-terminating decoding on malformed input
- Incorrect output: a stream that decompresses to something other than what the reference
  implementation produces
- Any way to make the compressor emit a stream that does not round-trip

The test suite fuzzes malformed input on every push and asserts that nothing but a `PkLibError`
comes back, so a crash or hang on hostile input is a real bug, not expected behaviour.

### Decompression bombs are the caller's responsibility

This is the one security-relevant limitation worth stating plainly, because it is a property of the
format rather than a defect.

The maximum expansion ratio measured against this implementation is **roughly 137:1**, consistent
across input sizes. A 72,680-byte compressed stream expands to 10,000,000 bytes. A one megabyte
hostile input can therefore produce around 137 MB of output.

The convenience overloads that return a `byte[]` accumulate the whole result in memory and impose
**no size limit**:

```csharp
byte[] restored = Exploder.Decompress(untrusted);   // unbounded: do not do this with hostile input
```

When decompressing anything you do not control, use the callback overload and enforce your own cap.
Throwing from the write callback aborts decoding promptly — it stops after the offending chunk,
having read only a fraction of the input, rather than completing the work and failing at the end:

```csharp
const int MaxBytes = 64 * 1024 * 1024;
int total = 0;

Exploder.Decompress(
    source.Read,
    chunk =>
    {
        total += chunk.Length;
        if (total > MaxBytes)
        {
            throw new InvalidDataException("Decompressed output exceeded the allowed size.");
        }

        destination.Write(chunk);
    });
```

Output arrives in 4096-byte blocks, so a cap is enforced to within one block.

### Out of scope

- Weaknesses in the PKWARE format itself. It is a 1989 compression scheme with no integrity or
  authenticity guarantees. It is not encryption, and `Crc32` is an error-detection checksum, not a
  cryptographic hash — neither detects deliberate tampering. Authenticate data by other means.
- Resource use that follows from a cap you chose not to apply, per the section above.
- Findings against a modified copy of this library.

## Upstream

The imploding and exploding algorithms are a port of PKLib by Ladislav Zezula, which ships as
[`src/pklib`](https://github.com/ladislav-zezula/StormLib/tree/master/src/pklib) inside
[StormLib](https://github.com/ladislav-zezula/StormLib).

A flaw in the shared algorithm, as opposed to this C# translation of it, likely affects the upstream
C implementation and every other port of it. Please say so in your report so it can be raised
upstream as well. Report it here first; coordinating disclosure is easier than un-publishing.

## How this project is checked

Every push and pull request to `main` runs, and must pass before merge:

- **SAST** — CodeQL over both the C# sources and the workflow files themselves, using the
  `security-extended` and `security-and-quality` query suites, plus a weekly re-scan so that
  newly published queries are applied to existing code
- **SCA** — a daily audit for known-vulnerable and deprecated NuGet packages, and a dependency
  review gate on every pull request
- **Secret scanning** — gitleaks and TruffleHog over the full commit history, alongside GitHub's
  native secret scanning with push protection
- **Supply chain** — every GitHub Action pinned to a full commit SHA rather than a mutable tag,
  kept current by Dependabot, with OpenSSF Scorecard auditing the repository's own posture

Findings are published to the repository's
[Security tab](https://github.com/AlyxSharkBite/PkLibSharp/security).

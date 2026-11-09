# Fixes made outside `sqlflow/` that SQLFlow should adopt

Some of `osdu/` started as copies of SQLFlow code (the file names the SQLFlow source it was copied from). A defect
found and fixed in one of those copies usually exists in SQLFlow too, but the fix lands in `osdu/` because this
project changes `sqlflow/` only for generic extension points (`CLAUDE.md`). This page lists those fixes, the SQLFlow
file that has the same weakness, and what carrying the fix upstream involves. Changes made inside `sqlflow/` itself are
in [sqlflow-changes.md](sqlflow-changes.md).

| Change | Fixed here | Same weakness upstream | Upstream |
| --- | --- | --- | --- |
| Happy Eyeballs | `osdu/src/SqlFlow.Delivery/Http/HttpClientBuilder.cs` | `src/SqlFlow.Acquire/Runtime/HttpClientBuilder.cs` (`KeepAliveConnectAsync`) | taken, SQLFlow `c18a657d` |
| A quoted name in a JSON path | `osdu/src/SqlFlow.Delivery/Json/JsonPathReader.cs` (`Parse`), with `RecordPathNotation.TryReadQuoted` | `src/SqlFlow.Acquire/Runtime/JsonPathReader.cs` (`Parse`) | not yet |

## Connection attempts raced across address families (Happy Eyeballs)

**What was wrong.** The HTTP stack opens every connection through its own `ConnectCallback` (for TCP keepalive, and
here also for the network policy). The callback tried the resolver's addresses strictly one after another, and all of
them shared one cancellation token: the handler's `ConnectTimeout` (30 s at most). On a network that gives the machine
a global IPv6 address but silently drops IPv6 traffic, the resolver lists the IPv6 addresses first. The first attempt
hung until Windows gave up on it (about 21 s), the second was cancelled when the 30 s ran out, and the cancellation is
not a `SocketException`, so the loop never moved on to an IPv4 address. Every retry repeated it until the request
timeout. Seen as: `HTTP request to https://login.microsoftonline.com/<tenant>/oauth2/v2.0/token timed out after 100s.
-> A connection could not be established within the configured ConnectTimeout`, while an IPv4 connect to the same host
succeeded in 30 ms.

**What it does now.** The callback follows RFC 8305:

- `Interleave` orders the addresses alternating between the family of the first one and the other, keeping the
  resolver's order within each family.
- `RaceAsync` starts an attempt on the first address, and one on the next each time 250 ms pass
  (`ConnectionAttemptDelay`) or an attempt fails. Earlier attempts keep running, so a slow but working address still
  wins. The first to connect is used; the others are cancelled, and a loser that connects anyway is disposed.
- Every address failing throws a `SocketException` carrying the last failure's error code and a message naming each
  address and why it failed. The handler's `ConnectTimeout` still cancels the whole race.

`RaceAsync` is generic over the connection type (`T : class, IDisposable`) and takes the attempt as a delegate, so its
tests (`osdu/tests/SqlFlow.Delivery.Tests/ConnectRaceTests.cs`) run without a network: a blackholed address, an
immediate failure, every address failing, the connect timeout cancelling all attempts, and a late loser being closed.

**Upstream.** SQLFlow's `KeepAliveConnectAsync` had the same sequential loop under one token, without the network
policy step. SQLFlow took the change as `c18a657d` (2026-10-10), with `ConnectRaceTests` beside it in
`tests/SqlFlow.Acquire.Tests`, and 1.0.0's `sqlflow/` carries it.

## A quoted name in a JSON path read to its closing quote

**What was wrong.** `JsonPathReader.Parse` ends a bracket step at the first `]` and trims quotes off what is inside, so a
name in quotes that holds a `]` (`tags['a]b']`) is cut short, and an escaped quote stays escaped (`data['it\'s']` reads
the name `it\'s`, backslash included). A record's `tags` can hold any name, so a check that reads one by its path read
nothing, or the wrong property.

**What it does now.** A bracket step whose first character, past blanks, is a quote is read to its closing quote with
RFC 9535's escapes undone (a quote, a backslash, `/`, `b`, `f`, `n`, `r`, `t`, `uXXXX`), then its `]`; an unclosed quote or
an unknown escape is refused naming the path. Every other step reads as before: `[*]`, `[n]`, a name in brackets without
quotes, and dotted names. The tests are in `osdu/tests/SqlFlow.Delivery.Tests/RecordPathNotationTests.cs`
(`A_json_path_reads_a_quoted_name_to_its_closing_quote`).

**Upstream.** SQLFlow's `JsonPathReader.Parse` has the same bracket step. Carrying the fix means moving the quoted-name
reader into `SqlFlow.Acquire` beside it (SQLFlow has no `RecordPathNotation`) and its tests into
`tests/SqlFlow.Acquire.Tests/RuntimeTests.cs`.

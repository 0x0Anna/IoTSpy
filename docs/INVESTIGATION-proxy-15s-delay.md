# Investigation: ~15 second delay on certain hosts through the IoTSpy proxy

## Status
**RESOLVED 2026-09-18.** Root cause: `ReadHttpMessageAsync` treated an explicit `Content-Length: 0` the same as "no Content-Length header at all" (both parsed to the int default `0`), so a bodyless-but-complete response (e.g. a `301` redirect with an empty body on a keep-alive connection — exactly what github.com/bing.com/youtube.com send) fell into the close-delimited (read-until-EOF) branch and blocked until the upstream's keep-alive idle timeout fired. Fixed by tracking header *presence* in a separate `hasContentLength` bool rather than inferring it from the parsed value. Applied in both `ExplicitProxyServer.cs` and `TransparentProxyServer.cs`. A related bug in the same function (HEAD responses carrying a `Content-Length` that will never actually arrive, causing an indefinite hang) was found and fixed in the same pass by threading the request method through and folding it into the existing `noBody` check. See the commit that references this file for full details. The rest of this document is preserved as the original problem statement for reference.

## Repo / environment
- Repo: this repository (IoTSpy), branch `feature/shared-upstream-connection-pool` (PR #108) — a shared upstream connection pool for the proxy, built on top of already-merged PRs #106/#107.
- Platform: Windows 10, .NET 10.
- The API auto-starts its explicit HTTP proxy on port 8888 if `ProxySettings.AutoStart = 1` in the local SQLite DB (`src/IoTSpy.Api/iotspy.db`). If testing a fresh checkout/worktree, set this via:
  ```sql
  UPDATE ProxySettings SET AutoStart = 1 WHERE Id = 1;
  ```
  before starting, or start it via the API/UI.
- Build: `dotnet build src/IoTSpy.Api/IoTSpy.Api.csproj`. Run: `dotnet run --project src/IoTSpy.Api`. The running process locks its own build output, so stop it before rebuilding.

## Symptom
A plain HTTP GET through the explicit proxy to certain hosts takes ~15 seconds to return a response, while other hosts return in well under a second. This is **not** intermittent noise — it reproduces consistently, every time, for the same set of hosts.

**Reproduction:**
```bash
# Slow (~15s):
curl -s -x http://127.0.0.1:8888 http://github.com/  -o /dev/null -w "http_code=%{http_code} time=%{time_total}\n"
curl -s -x http://127.0.0.1:8888 http://bing.com/    -o /dev/null -w "http_code=%{http_code} time=%{time_total}\n"
curl -s -x http://127.0.0.1:8888 http://youtube.com/ -o /dev/null -w "http_code=%{http_code} time=%{time_total}\n"

# Fast (<1s):
curl -s -x http://127.0.0.1:8888 http://google.com/    -o /dev/null -w "http_code=%{http_code} time=%{time_total}\n"
curl -s -x http://127.0.0.1:8888 http://wikipedia.org/ -o /dev/null -w "http_code=%{http_code} time=%{time_total}\n"
curl -s -x http://127.0.0.1:8888 http://reddit.com/    -o /dev/null -w "http_code=%{http_code} time=%{time_total}\n"
curl -s -x http://127.0.0.1:8888 http://example.com/   -o /dev/null -w "http_code=%{http_code} time=%{time_total}\n"
```

Direct (non-proxied) `curl` to the *same slow hosts* from the *same machine* is instant (<200ms):
```bash
curl -sv http://github.com/ -o /dev/null -w "http_code=%{http_code} time=%{time_total}\n"
```
This proves the network path and DNS itself are fine — the delay is introduced by the proxy process.

## The most important clue

Stopwatch instrumentation was added directly around the DNS-resolve + TCP-connect step, inside `UpstreamConnectionPool.CreateAsync` in `src/IoTSpy.Proxy/Resilience/UpstreamConnectionPool.cs` (look for a `logger.LogWarning("DIAG CreateAsync ...")` line — it may still be present, or may have been cleaned up; check git history / diff against origin/main on this branch if it's gone).

Captured real sample for `github.com`:
```
DIAG CreateAsync github.com:80 resolve=104ms connect=50ms addr=140.82.112.3
```
i.e. DNS resolution took 104ms, the TCP connect took 50ms — **154ms total** for connection setup — yet the client-observed total request time for that same request was **15.46 seconds**.

**This proves the delay is NOT in DNS resolution or the TCP connect.** It is happening somewhere between "connection established" and "response delivered to the client" — i.e. inside request-write / response-read / per-request processing, not connection setup.

## Already ruled out — do not re-investigate these

1. **Not a regression from the connection-pooling work.** Confirmed via a direct A/B test: checked out unmodified `origin/main` (before any pooling changes) into a separate worktree, ran the identical repro, got the identical ~15-20s delay for the identical hosts. The bug predates this session's changes entirely.
2. **Not Windows Defender / SmartScreen.** `Get-MpComputerStatus` on this machine shows `RealTimeProtectionEnabled: False`, `NISEnabled: False`, `BehaviorMonitorEnabled: False`. No real-time network inspection is active.
3. **Not DNS resolution.** Proven by the 104ms figure above. Two fix attempts were made and abandoned because they had zero effect on timing:
   - Explicitly resolving via `Dns.GetHostAddressesAsync` and preferring an IPv4 address, then connecting to that concrete `IPAddress` instead of using `TcpClient.ConnectAsync(hostname, port)` — no change, still ~15.1s.
   - Restricting resolution to `AddressFamily.InterNetwork` only (`Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)`) to rule out a slow/dropped AAAA query — no change, still ~15.1s.
4. **Not the TCP connect itself.** Proven by the 50ms figure above.
5. **Circuit breaker / retry pipeline theory is weakened but not fully eliminated.** The delay is suspiciously close to `ResilienceOptions.ConnectTimeoutSeconds` (default 15, in `src/IoTSpy.Core/Models/ResilienceOptions.cs`) and the Polly retry/circuit-breaker config in `src/IoTSpy.Proxy/Resilience/ProxyResiliencePipelines.cs` (`RetryCount: 2`, exponential backoff base 0.5s, `TlsHandshakeTimeoutSeconds` default 10). However, the DIAG log shows the connect-pipeline (`perHostPipelines.GetPipeline(host,port).ExecuteAsync(...)`, which wraps exactly the DNS+connect step) completing in 154ms — so if a Polly timeout is involved, it is **not** that pipeline's own timeout, since that one clearly succeeded fast on the first attempt. The repro here is plain HTTP (no TLS), so the TLS handshake pipeline (`ProxyResiliencePipelines.TlsPipelineKey`) is not exercised either. If a Polly pipeline is still implicated, it must be a *different* one, or the ~15s coincidence with `ConnectTimeoutSeconds` may simply be that — a coincidence.

## Where to look next

- `src/IoTSpy.Proxy/Interception/ExplicitProxyServer.cs`:
  - `HandlePlainHttpAsync` — entry point for this exact repro (plain `http://` through the explicit proxy).
  - `InterceptHttpStreamAsync` — the shared per-request loop.
  - The `WriteAndReadWithRetryAsync` local function inside it — writes the request via `WriteHttpMessageAsync`, reads the response via `ReadHttpMessageAsync`. **Add Stopwatch instrumentation around the write call and the read call separately** to find out which one actually consumes the 15s.
  - `ReadHttpMessageAsync`, `ReadLineAsync`, `ReadChunkedBodyAsync`, `WriteHttpMessageAsync` (private static methods near the bottom of the file) — look for anything that could block indefinitely or until a timeout: a `Content-Length` mismatch causing `ReadExactlyAsync` to wait for bytes that never arrive, a chunked-encoding parse edge case, a case where the server sends a partial response then pauses.
- **Compare raw response bytes** between a slow host and a fast host. e.g.:
  ```bash
  curl -v http://github.com/ -o /dev/null 2>&1 | head -40
  curl -v http://google.com/ -o /dev/null 2>&1 | head -40
  ```
  Look for differences in framing: `Transfer-Encoding: chunked` vs `Content-Length`, `Connection: keep-alive` vs `close`, unusual header ordering, HTTP/1.0 vs HTTP/1.1, anything that our hand-rolled HTTP parser (`ReadHttpMessageAsync`) might handle differently than curl's parser.
- **Check whether the same 15s delay reproduces through `TransparentProxyServer.cs`'s equivalent code path** (`HandlePlainTransparentAsync` → its own `InterceptHttpStreamAsync`, duplicated logic from the file above). If it does, the bug is in the shared parsing logic (duplicated across both files). If it doesn't, the bug is specific to `ExplicitProxyServer`'s dispatch/handling.
- Also worth checking: is this specific to **plain HTTP** (`http://`) or does it also affect the HTTPS/TLS-MITM path (`https://github.com/` through `HandleConnectAsync`)? If HTTPS is unaffected, that further narrows the search to `HandlePlainHttpAsync`'s specific code path rather than the shared response-reading logic used by both.

## What a fix needs to explain
1. The precise operation where the 15 seconds is actually spent (not DNS, not connect — proven).
2. Why it's specific to `github.com` / `bing.com` / `youtube.com` and not `google.com` / `wikipedia.org` / `reddit.com` / `example.com` — what's different about those hosts' responses or connection behavior.
3. A fix, or at minimum a next concrete diagnostic step if the root cause is still unclear after investigation.

## Prior investigation history (for context, not to be repeated)
This bug was chased for a long time earlier in the session that produced this document, initially under the mistaken assumption it was DNS/IPv6-related (a plausible-sounding but ultimately disproven theory — a Windows/ISP combination where IPv6 is configured but non-functional can cause exactly this kind of multi-second first-connection delay via `TcpClient.ConnectAsync(hostname, port)`'s internal dual-stack resolution; this was a reasonable hypothesis but the Stopwatch data above disproves it for this specific bug). An automated investigation using a different model (Claude Opus, via a background agent) was also dispatched in parallel with the same problem statement — check whether that investigation has already produced a result before duplicating effort.

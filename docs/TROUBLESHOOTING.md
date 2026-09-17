# IoTSpy — Troubleshooting Guide

Solutions for common problems agents and developers encounter during development. For production incident response (proxy outages, database corruption, secret rotation, certificate issues), see [RUNBOOK.md](RUNBOOK.md) instead.

---

## Table of Contents
1. [Setup & Environment](#setup--environment)
2. [Build & Compile Errors](#build--compile-errors)
3. [Test Failures](#test-failures)
4. [Runtime Errors](#runtime-errors)
5. [Database Issues](#database-issues)
6. [Frontend Issues](#frontend-issues)
7. [API & Network Issues](#api--network-issues)
8. [Feature-Specific Issues](#feature-specific-issues)

---

## Setup & Environment

### ❌ "Auth__JwtSecret not set"
**Error:** `ArgumentException: JwtSecret must be at least 32 characters`

**Solution:**
```bash
export Auth__JwtSecret="your-32-character-or-longer-secret-key"
dotnet run --project src/IoTSpy.Api
```

**Check:** `echo $Auth__JwtSecret` should return the secret

---

### ❌ "Port 5000 already in use"
**Error:** `System.IO.IOException: Unable to bind to [::]:5000...`

**Solution:**
```bash
# Find what's using the port
lsof -i :5000       # macOS/Linux
netstat -ano | findstr :5000  # Windows

# Kill the process
kill -9 <PID>       # macOS/Linux
taskkill /PID <PID> /F  # Windows

# Or use a different port
export Urls="http://localhost:5001"
```

---

### ❌ "dotnet command not found"
**Error:** `command not found: dotnet`

**Solution:**
```bash
# Check if .NET is installed
dotnet --version

# If not installed, download from https://dotnet.microsoft.com/download
# Then restart your terminal

# On macOS with Homebrew
brew install dotnet
```

---

## Build & Compile Errors

### ❌ "The type or namespace name 'X' does not exist"
**Cause:** Missing `using` statement or incorrect reference

**Solution:**
1. Add missing `using` statement at top of file
2. Check project references (right-click project → Edit Project File)
3. Run `dotnet build` to see full error
4. Verify assembly name matches: `IoTSpy.Core`, `IoTSpy.Storage`, etc.

---

### ❌ "The name 'DbContext' does not exist in the current context"
**Cause:** `IoTSpyDbContext` not injected or `using` statement missing

**Solution:**
```csharp
// Add using
using IoTSpy.Storage;

// In constructor
public MyService(IoTSpyDbContext db) => _db = db;

// Or inject repository instead
public MyService(IMyRepository repo) => _repo = repo;
```

---

### ❌ "Build fails with 'the SDK version X is not supported'"
**Error:** `A compatible SDK version for global.json version X could not be found`

**Solution:**
```bash
# Check required .NET version
cat global.json  # Look for sdkVersion

# Install the correct version
dotnet --list-sdks  # See what's installed

# Download from https://dotnet.microsoft.com/download
# Then verify
dotnet --version
```

---

## Test Failures

### ❌ "Test timeout / Operation cancelled"
**Cause:** Test taking too long, usually EF Core in-memory SQLite

**Solution:**
```csharp
// Increase timeout
[Fact(Timeout = 10000)]  // 10 seconds
public async Task MyTest() { ... }

// Or increase globally (xUnit)
// In xunit.runner.json:
// { "longRunningTestSeconds": 10 }

// Reduce test data
// Use smaller datasets in integration tests

// Run tests sequentially (not in parallel)
dotnet test --no-build -- -parallel none
```

---

### ❌ "ArgumentNullException: value cannot be null"
**Cause:** Null reference in test setup or mock configuration

**Solution:**
```csharp
// Verify mocks are set up
var mockRepo = Substitute.For<IDeviceRepository>();
mockRepo.GetAllAsync(Arg.Any<CancellationToken>())
    .Returns(Task.FromResult(new List<Device> { ... }));  // ✅ Returns a value

// Check NSubstitute syntax
// .Returns() for sync, .Returns(Task.FromResult(...)) for async

// Verify dependencies are injected
var controller = new MyController(mockRepo);  // Check all params
```

---

### ❌ "NSubstitute threw an exception when trying to configure a substitute"
**Cause:** Trying to substitute a concrete type instead of interface

**Solution:**
```csharp
// ❌ Wrong
var mock = Substitute.For<ConcreteClass>();

// ✅ Correct
var mock = Substitute.For<IInterface>();
var concrete = new ConcreteClass(mock);
```

---

### ❌ "DbContext has been disposed"
**Cause:** Using DbContext outside of scope

**Solution:**
```csharp
// ✅ Correct: Repository is scoped, gets fresh DbContext per request
public class MyController(IDeviceRepository repo) { ... }

// ❌ Wrong: Reusing DbContext across requests
public class MyService
{
    private readonly IoTSpyDbContext _db;  // Bad if not scoped
}

// If you need multiple queries in one operation, use UnitOfWork pattern
// or single repository call
```

---

## Runtime Errors

### ❌ "Timeline crash" — Frontend displays garbage
**Error:** Numeric enum values in capture list, timeline component crashes

**Cause:** Missing `JsonStringEnumConverter` on SignalR

**Solution:**
Verify in `Program.cs`:
```csharp
services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter()));  // ✅ Required

services
    .AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(
        new JsonStringEnumConverter()));  // ✅ ALSO required
```

Both calls are required. Missing the SignalR call causes numeric enum serialization.

---

### ❌ "Packet capture unavailable" warning
**Error:** "SharpPcap initialization failed" in API logs

**Cause:** Missing `CAP_NET_RAW` capability on Linux, or Npcap not installed on Windows

**Solution (Linux):**
```bash
# Install libpcap
sudo apt-get install libpcap-dev

# Grant capabilities to dotnet binary (NOT symlink)
sudo setcap cap_net_raw,cap_net_admin+eip "$(readlink -f $(which dotnet))"

# Verify
getcap "$(readlink -f $(which dotnet))"
# Should show: cap_net_raw,cap_net_admin+eip

# Restart API
kill %1  # Kill running API
dotnet run --project src/IoTSpy.Api
```

**Solution (Windows):**
1. Download Npcap from https://npcap.com
2. Install (defaults are fine)
3. Restart API
4. Packet capture should now work

**Solution (macOS):**
- System should grant permission on first packet capture attempt
- Check System Settings → Privacy & Security → Full Disk Access for terminal app

---

### ❌ "SSL certificate rejected" on iOS
**Error:** iOS device rejects IoTSpy CA certificate

**Cause:** Certificate validity > 397 days, or AKI in wrong format, or IP SAN format incorrect

**Solution:**
```bash
# Delete all certs (leaf certs will be regenerated)
sqlite3 src/IoTSpy.Api/iotspy.db "DELETE FROM Certificates WHERE IsRootCa = 0;"

# Optionally delete root CA to force regeneration
sqlite3 src/IoTSpy.Api/iotspy.db "DELETE FROM Certificates WHERE IsRootCa = 1;"

# Restart API (fresh certs will be generated)
dotnet run --project src/IoTSpy.Api

# Download & install new CA certificate on iOS
# Go to: http://[your-api]:5000/api/certificates/root-ca/download
```

See [DESIGN-DECISIONS.md](DESIGN-DECISIONS.md#iosmacos-tls-certificate-compatibility) for why this is needed.

---

## Database Issues

### ❌ "SQLite database is locked"
**Error:** `SQLitePCL.raw.sqlite3.SQLiteException: database is locked`

**Cause:** Multiple processes accessing SQLite simultaneously

**Solution:**
```bash
# Close all API instances
ps aux | grep "IoTSpy.Api"
kill <PID>

# If persistent, check for corrupted lock file
rm src/IoTSpy.Api/iotspy.db-shm
rm src/IoTSpy.Api/iotspy.db-wal

# Restart API
dotnet run --project src/IoTSpy.Api
```

---

### ❌ "Migration failed" — EF Core error
**Error:** `Unexpected character '@' at line X, column Y`

**Cause:** SQL syntax error in migration (usually SQLite-specific)

**Solution:**
```bash
# Check the migration file
nano src/IoTSpy.Storage/Migrations/<timestamp>_*.cs

# For SQLite, use migrationBuilder.Sql() for complex operations
migrationBuilder.Sql(@"
    UPDATE Captures SET RequestBodySize = 0 WHERE RequestBodySize IS NULL;
");

# For ALTER COLUMN, use Sql() instead of AlterColumn on SQLite
# See src/IoTSpy.Storage/Migrations/AddBodyCaptureDefaults.cs for example

# Revert migration
dotnet ef migrations remove \
    --project src/IoTSpy.Storage \
    --startup-project src/IoTSpy.Api

# Fix and try again
# dotnet ef migrations add FixedName ...
```

---

### ❌ "Cannot convert from 'bool' to 'long'"
**Error:** DateTimeOffset serialization issue

**Cause:** Missing `ValueConverter` for DateTimeOffset

**Solution:**
Verify in `IoTSpyDbContext.ConfigureConventions()`:
```csharp
protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
{
    configurationBuilder
        .Properties<DateTimeOffset>()
        .HaveConversion(
            x => x.ToUnixTimeMilliseconds(),  // Store as long
            x => DateTimeOffset.FromUnixTimeMilliseconds(x));  // Read as DateTimeOffset
}
```

---

## Frontend Issues

### ❌ "Blank page on localhost:3000"
**Error:** Frontend appears empty or shows error overlay

**Solution:**
```bash
# Check console (F12 → Console tab)
# Look for 404 on /api/* calls

# Verify backend is running
curl http://localhost:5000/api/auth/status

# Check CORS
# In browser console, look for "CORS error"
# Solution: Verify Frontend:Origin in appsettings.json matches localhost:3000

# Restart frontend dev server
cd frontend
npm run dev  # Should see "Local: http://localhost:3000"
```

---

### ❌ "Cannot GET /api/..."
**Error:** Frontend shows 404 when accessing API

**Cause:** Backend not running, or CORS misconfigured

**Solution:**
```bash
# Verify backend is running
curl http://localhost:5000/api/auth/status  # Should return JSON

# Check appsettings.json CORS
cat src/IoTSpy.Api/appsettings.Development.json | grep -A 2 "Frontend"
# Should have "Origin": "http://localhost:3000"

# If changed, restart backend
kill %1 && dotnet run --project src/IoTSpy.Api
```

---

### ❌ "React hook errors in console"
**Error:** "Error: Too many re-renders" or "Missing dependency"

**Solution:**
1. Check console (F12 → Console)
2. Look for "in useEffect dependencies" warnings
3. Add missing dependency to hook's dependency array:
   ```typescript
   useEffect(() => {
       // code
   }, [dependency1, dependency2]);  // Add all used variables
   ```
4. Use `useCallback` to memoize functions passed as dependencies

---

## API & Network Issues

### ❌ "Connection refused" — Cannot connect to API
**Error:** `Failed to fetch` or `ECONNREFUSED 127.0.0.1:5000`

**Solution:**
```bash
# Check API is running
ps aux | grep "dotnet run"

# Check port is listening
lsof -i :5000  # macOS/Linux

# If API is running but port shows closed:
# Kill and restart
kill <PID>
export Auth__JwtSecret="..." 
dotnet run --project src/IoTSpy.Api
```

---

### ❌ "Unauthorized (401)" — JWT token expired or invalid
**Error:** `{ "error": "Unauthorized" }`

**Solution:**
```bash
# Get new token
curl -X POST http://localhost:5000/api/auth/login \
    -H "Content-Type: application/json" \
    -d '{"username":"admin","password":"your-password"}'

# Use token in header
curl http://localhost:5000/api/devices \
    -H "Authorization: Bearer <token>"

# On frontend, check localStorage
localStorage.getItem('iotspy_token')

# If missing, logout and login again
```

---

### ❌ "Gateway Timeout (504)" — Proxy server slow or hung
**Error:** API times out after 30s

**Cause:** Upstream host is slow or unreachable, or rule/script stuck in loop

**Solution:**
```bash
# Check resilience config in appsettings.json
# Increase timeouts if needed:
"Resilience": {
    "ConnectTimeoutSeconds": 15,
    "TlsHandshakeTimeoutSeconds": 10,
    // Increase these if dealing with slow IoT devices
}

# Restart API
kill %1 && dotnet run --project src/IoTSpy.Api

# Disable problematic rules/scripts temporarily
# Via API: DELETE /api/manipulation/rules/{id}
```

---

## Feature-Specific Issues

### ❌ "Rules not applying" — Traffic passes through unchanged
**Cause:** Rule condition doesn't match, or rule disabled

**Solution:**
```bash
# Check rule is enabled
curl http://localhost:5000/api/manipulation/rules \
    -H "Authorization: Bearer $TOKEN" | jq '.[] | {id, isEnabled, hostPattern}'

# Enable if disabled
curl -X PUT http://localhost:5000/api/manipulation/rules/{id} \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    -d '{"isEnabled": true}'

# Check pattern matches
# Rule hostPattern should match request host (regex)
# E.g., "*.example.com" or "example.com" (regex, not glob)
```

---

### ❌ "Fuzzer hanging" — Fuzzer job never completes
**Cause:** Job stuck or infinite loop in mutation

**Solution:**
```bash
# Cancel the job
curl -X DELETE http://localhost:5000/api/manipulation/fuzzer/{jobId} \
    -H "Authorization: Bearer $TOKEN"

# Check fuzzer config
# In FuzzerService, verify concurrency limit (default 5)

# Reduce payload size or iteration count for large requests
```

---

### ❌ "SignalR connection dropped frequently"
**Error:** "Connection lost" message in frontend

**Cause:** Network unstable, or server restarting, or too many concurrent connections

**Solution:**
```typescript
// Frontend: Check reconnection logic in useTrafficStream hook
const connection = new HubConnectionBuilder()
    .withAutomaticReconnect([0, 1000, 5000])  // Backoff delays
    .build();

// Backend: Check appsettings.json
// Increase connection pool size if many clients
```

---

### ❌ "API spec generation empty" — No endpoints detected
**Cause:** No captures matching filter

**Solution:**
```bash
# Ensure captures exist
curl http://localhost:5000/api/captures \
    -H "Authorization: Bearer $TOKEN" \
    | jq 'length'

# Generate spec with no filters
curl -X POST http://localhost:5000/api/apispec/generate \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    -d '{"name":"My API", "host":"example.com"}'

# If still empty, check capture host matches
```

---

## Getting More Help

1. **Check logs** — API logs to console + `Serilog` rolling file (check `logs/` directory)
2. **Search code** — `grep -r "error message"` to find where it's thrown
3. **Check existing issues** — GitHub issues may have similar problems
4. **Ask in PR comments** — Link to this document + the specific section

See [AGENT-NOTES.md](AGENT-NOTES.md#common-debugging-scenarios) for more debug tips.

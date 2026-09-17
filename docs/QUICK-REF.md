# IoTSpy — Quick Reference

Common commands and shortcuts for daily development.

---

## Setup (One-time)

```bash
# Clone & navigate
git clone <repo-url> && cd IoTSpy

# Set JWT secret via .NET user secrets (stored in OS user profile, not the repo)
dotnet user-secrets set "Auth:JwtSecret" "your-32-char-minimum-secret" \
  --project src/IoTSpy.Api

# Backend dependencies
dotnet build

# Frontend dependencies
cd frontend && npm install && cd ..
```

---

## Daily Development

### Start everything
```bash
# Terminal 1: Backend (auto-reload on file changes; user secrets loaded automatically)
dotnet watch --project src/IoTSpy.Api

# Terminal 2: Frontend
cd frontend && npm run dev

# Terminal 3: Run tests (optional, watch mode)
dotnet test --watch
```

**Access points:**
- API: `http://localhost:5000`
- Frontend: `http://localhost:5173` (Vite default)
- API docs (dev): `http://localhost:5000/scalar`
- Proxy: `:8888` (explicit mode, `:9999` transparent)

### VS Code (alternative to terminal)
The repo ships `.vscode/launch.json` and `.vscode/tasks.json`. These files are committed and safe to use as-is.

| Launch config | What it does |
|---|---|
| **Full Stack: API + Frontend** | Starts both with one press of F5 |
| **API: Debug (IoTSpy.Api)** | API only, C# debugger attached |
| **Frontend: Vite Dev Server** | Frontend only |
| **Frontend: Attach Chrome Debugger** | Attach to Chrome on port 9222 for JS breakpoints |

User secrets (`Auth:JwtSecret`) are picked up automatically — no env var needed.

---

## Build & Test

```bash
# Full build
dotnet build

# Build single project
dotnet build src/IoTSpy.Protocols/

# Run all tests
dotnet test

# Run single test project
dotnet test src/IoTSpy.Proxy.Tests/IoTSpy.Proxy.Tests.csproj

# Run specific test class
dotnet test --filter "ClassName=TlsClientHelloParserTests"

# Run with coverage report
dotnet test /p:CollectCoverage=true /p:CoverageFormat=lcov

# Frontend tests
cd frontend && npm test

# Frontend build (production)
cd frontend && npm run build

# Clean everything
dotnet clean && rm -rf ~/.nuget/packages && cd frontend && rm -rf node_modules dist
```

---

## Git Workflow

```bash
# Branch naming
git checkout -b feature/short-description
# or
git checkout -b fix/issue-number

# Stage & commit — stage specific files, not `git add .`/`-A`, to avoid sweeping in
# unrelated or sensitive files
git add <specific-files>
git commit -m "Brief summary

Detailed explanation if needed."

# Push
git push -u origin feature/short-description

# Check status
git status
git log --oneline -10

# Revert local changes
git restore <file>
git checkout -- .

# Amend last commit (before push)
git add . && git commit --amend --no-edit
```

---

## Database Operations

```bash
# Create a migration
dotnet ef migrations add MigrationName \
  --project src/IoTSpy.Storage \
  --startup-project src/IoTSpy.Api

# List migrations
dotnet ef migrations list \
  --project src/IoTSpy.Storage \
  --startup-project src/IoTSpy.Api

# Apply migrations (auto on app start, or manually)
dotnet ef database update \
  --project src/IoTSpy.Storage \
  --startup-project src/IoTSpy.Api

# View database schema (SQLite)
sqlite3 src/IoTSpy.Api/iotspy.db ".schema"

# Query database (SQLite)
sqlite3 src/IoTSpy.Api/iotspy.db "SELECT * FROM Devices LIMIT 5;"

# Drop all local data (SQLite)
rm src/IoTSpy.Api/iotspy.db
dotnet run --project src/IoTSpy.Api  # Will recreate fresh DB

# Switch to Postgres (dev)
# 1. Install Postgres locally or via Docker
# 2. Create database: createdb iotspy
# 3. Set connection string:
export Database__Provider="postgres"
export Database__ConnectionString="Host=localhost;Database=iotspy;Username=postgres;Password=..."
```

---

## Docker

```bash
# Build image
docker build -t iotspy:latest .

# Run with compose (dev)
docker compose up -d

# View logs
docker compose logs -f iotspy-api

# Stop
docker compose down

# Clean up
docker compose down -v  # with volumes
docker system prune -a  # remove unused images/containers
```

---

## Code Navigation (Search)

```bash
# Find a class/interface
grep -r "class DeviceRepository" src/

# Find all implementations of an interface
grep -r "IDeviceRepository" src/

# Find method calls
grep -r "\.GetByIdAsync" src/

# Find all TODO/FIXME comments
grep -rn "TODO\|FIXME" src/ | head -20

# Full code search (ripgrep, if installed)
rg "IProtocolDecoder" --type cs
rg "async Task<" src/ -l  # List files with async methods
```

---

## API Testing (curl/Postman)

```bash
# Get auth status
curl http://localhost:5000/api/auth/status

# Set password (one-time)
curl -X POST http://localhost:5000/api/auth/setup \
  -H "Content-Type: application/json" \
  -d '{"password":"your-password"}'

# Login
curl -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"your-password"}' | jq .token

# Export token to variable
TOKEN=$(curl -s -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"your-password"}' | jq -r .token)

# Get devices (with auth)
curl http://localhost:5000/api/devices \
  -H "Authorization: Bearer $TOKEN"

# Start proxy
curl -X POST http://localhost:5000/api/proxy/start \
  -H "Authorization: Bearer $TOKEN"

# Get proxy status
curl http://localhost:5000/api/proxy/status \
  -H "Authorization: Bearer $TOKEN"
```

---

## Documentation Navigation

```bash
# Where should I start?
→ See docs/PLAN-INDEX.md (main entry point)

# I want to add a feature
→ See docs/CODE-PATTERNS.md ("Where Does My Code Go?")
→ See docs/AGENT-NOTES.md ("Adding Features: Checklist")

# I'm debugging a problem
→ See docs/AGENT-NOTES.md ("Common Debugging Scenarios")
→ See docs/TROUBLESHOOTING.md

# I need to understand the architecture
→ See docs/DESIGN-DECISIONS.md
→ See docs/ARCHITECTURE.md

# What's the current status?
→ See docs/PLAN-INDEX.md (quick status table)
→ See docs/PHASES-COMPLETED.md (detailed phase list)

# What should I work on next?
→ See docs/CODE-REVIEW-FINDINGS.md — closed/archived, but its three deliberately-deferred items (#48, #56, #59) are candidates if picking one back up
→ See docs/GAPS.md (known issues & tech debt)
→ See docs/PHASES-ROADMAP.md (larger future enhancement areas)
```

---

## Common Recipes

### Add a new protocol decoder
```bash
# 1. Create model in Core
touch src/IoTSpy.Core/Models/MyMessage.cs

# 2. Create decoder in Protocols
touch src/IoTSpy.Protocols/MyProtocol/MyProtocolDecoder.cs

# 3. Register in Program.cs
# services.AddSingleton<IProtocolDecoder<MyMessage>, MyProtocolDecoder>();

# 4. Add tests
touch src/IoTSpy.Protocols.Tests/MyProtocolDecoderTests.cs

# 5. Test
dotnet test src/IoTSpy.Protocols.Tests/

# See CODE-PATTERNS.md for full example
```

### Add a new REST endpoint
```bash
# 1. Add method to controller (or create new controller)
nano src/IoTSpy.Api/Controllers/MyController.cs

# 2. Build & test
dotnet build
dotnet test

# 3. Try in Scalar docs (http://localhost:5000/scalar)

# See CODE-PATTERNS.md for full example
```

### Add a new database entity
```bash
# 1. Create model in Core/Models/
nano src/IoTSpy.Core/Models/MyEntity.cs

# 2. Add DbSet to DbContext
nano src/IoTSpy.Storage/IoTSpyDbContext.cs
# public DbSet<MyEntity> MyEntities { get; set; }

# 3. Create migration
dotnet ef migrations add AddMyEntity \
  --project src/IoTSpy.Storage \
  --startup-project src/IoTSpy.Api

# 4. Review migration file
nano src/IoTSpy.Storage/Migrations/<timestamp>_AddMyEntity.cs

# 5. Apply
dotnet ef database update \
  --project src/IoTSpy.Storage \
  --startup-project src/IoTSpy.Api

# 6. Create repository (see CODE-PATTERNS.md)
```

### Prepare a PR
```bash
# 1. Run all tests locally
dotnet test
cd frontend && npm test && npm run build && cd ..

# 2. Build
dotnet build

# 3. Review changes
git diff HEAD~1

# 4. Commit (see Git Workflow above)
git add . && git commit -m "..."

# 5. Push
git push origin your-branch

# 6. Create PR on GitHub
# (link the PLAN-INDEX.md documentation URL at end of PR description)
```

---

## Debugging Tips

### App won't start
```bash
# Check user secrets are set
dotnet user-secrets list --project src/IoTSpy.Api

# Check port is free
lsof -i :5000  # Linux/macOS
netstat -ano | findstr :5000  # Windows

# Clean build
dotnet clean && dotnet build
```

### Tests hang/timeout
```bash
# Run single test with verbose output
dotnet test -v d --filter "TestName=MyTest"

# Increase timeout (in test class)
// [Fact(Timeout = 5000)]

# Check for deadlocks (async/await issues)
```

### Frontend not updating
```bash
# Hard refresh browser
Ctrl+Shift+R (Chrome/Firefox)
Cmd+Shift+R (macOS Safari)

# Clear npm cache
npm cache clean --force

# Reinstall dependencies
cd frontend && rm -rf node_modules package-lock.json && npm install
```

### Database corruption
```bash
# Backup & reset (SQLite)
cp src/IoTSpy.Api/iotspy.db src/IoTSpy.Api/iotspy.db.backup
rm src/IoTSpy.Api/iotspy.db

# Rebuild
dotnet build
dotnet run --project src/IoTSpy.Api
```

---

## Performance Profiling

```bash
# Backend: Measure test execution
dotnet test /p:LogLevel=Verbose

# Frontend: Check bundle size
cd frontend && npm run build && npm run build-stats

# Check API response time (curl)
time curl http://localhost:5000/api/devices

# Database: Slow query log (Postgres)
# SET log_min_duration_statement = 1000;  -- Log queries > 1s
```

---

## Cleanup

```bash
# Remove build artifacts
dotnet clean
rm -rf bin/ obj/

# Remove frontend artifacts
cd frontend && rm -rf node_modules dist build

# Reset to clean state (careful!)
git clean -fdx
git reset --hard HEAD

# Remove untracked files
git clean -fd
```

---

## Useful Tools

| Tool | Command | Use |
|---|---|---|
| ripgrep | `rg "pattern"` | Fast code search |
| jq | `curl ... | jq .field` | JSON parsing |
| sqlite3 | `sqlite3 iotspy.db` | Direct DB queries |
| dotnet-outdated | `dotnet outdated` | Check NuGet updates |
| BenchmarkDotNet | `[Benchmark]` | Performance testing |

---

## Links

- **API Docs (dev only):** http://localhost:5000/scalar
- **Frontend:** http://localhost:3000
- **GitHub:** [repo-url]
- **Code Patterns:** `docs/CODE-PATTERNS.md`

See **[PLAN-INDEX.md](PLAN-INDEX.md)** for navigation to all documentation.

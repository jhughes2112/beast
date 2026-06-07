# Beast Project Memory

## Build & Run

```bash
# Full build: beast.exe + beastagent Docker image
build.bat

# Build only Beast CLI
dotnet publish Beast\Beast.csproj -c Release -r win-x64 --self-contained -o build\bin\release\beast-win-x64

# Run Agent directly for debugging
dotnet run --project Agent\Agent.csproj -- --debug
```

## Code Completion Handling

**Location:** `Beast/Display/DisplayScreen.cs` lines 1607-1650 (Enter handler), 1658-1709 (Tab handler)

The input area supports command completion via Tab and Enter:
- **Tab**: Cycles through or accepts highlighted completion from popup
- **Enter** (modified): If a completion popup is active (`_completionActive && _completionMatches.Count > 0`), Enter first accepts the highlighted entry by replacing `inputBuffer` with the match, then submits. If no popup is active, Enter submits as before.

Completion popup state variables: `_completionActive`, `_completionMatches` (List<string>), `_completionIndex`
Match index for Tab cycling: `matchIndex`
Completion mode flag: `inCompletion` (true when inline completion has been cycled via Tab without popup)

## Coding Standards

- No `var`, no LINQ, no default parameters
- Opening brace on its own line (ANSI braces)
- Single return at bottom of functions
- No partial classes, no setters
- Explicit `using` directives (no ImplicitUsings)
- String interpolation over concatenation
- Short, cohesive methods with one-line comments only

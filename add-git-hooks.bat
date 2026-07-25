@echo off
setlocal EnableExtensions
rem add-git-hooks.bat -- installs this repo's git hooks.
rem
rem Self-contained on purpose: the hook scripts live at the BOTTOM of this same
rem file, after the ":::HOOK <name>" marker lines. Drop this one file into any
rem repository, run it, and the hooks are installed -- nothing else has to come
rem along with it. To change a hook, edit the payload down there.
rem
rem Safe to run repeatedly: a hook is rewritten only when it differs.

pushd "%~dp0"

where git >nul 2>&1
if errorlevel 1 (
	echo ERROR: git is not on PATH.
	goto :fail
)

rem The hooks shell out to dotnet and unfugly.exe. Installing them when either
rem is missing just breaks every later commit, so refuse up front.
set "MISSINGTOOL="
where dotnet >nul 2>&1
if errorlevel 1 (
	echo ERROR: dotnet is not on PATH.
	set "MISSINGTOOL=1"
)
where unfugly.exe >nul 2>&1
if errorlevel 1 (
	echo ERROR: unfugly.exe is not on PATH.
	set "MISSINGTOOL=1"
)
if defined MISSINGTOOL (
	echo No hooks were installed.
	goto :fail
)

rem --git-common-dir, not --git-dir, so this still targets the shared hooks
rem directory when run from inside a linked worktree.
set "GITDIR="
for /f "delims=" %%d in ('git rev-parse --git-common-dir 2^>nul') do set "GITDIR=%%d"
if not defined GITDIR (
	echo ERROR: %CD% is not a git repository.
	goto :fail
)

set "HOOKDSTDIR=%GITDIR%\hooks"
if not exist "%HOOKDSTDIR%" mkdir "%HOOKDSTDIR%"

rem core.hooksPath, if set, silently overrides .git/hooks -- warn rather than
rem install hooks that would never run.
set "WARNED="
set "HOOKSPATH="
for /f "delims=" %%p in ('git config --get core.hooksPath 2^>nul') do set "HOOKSPATH=%%p"
if defined HOOKSPATH (
	echo WARNING: core.hooksPath is set to "%HOOKSPATH%".
	echo          Hooks in "%HOOKDSTDIR%" will be ignored by git.
	echo          Clear it with: git config --unset core.hooksPath
	echo.
	set "WARNED=1"
)

echo Installing hooks into %HOOKDSTDIR%

rem Splits this file on its :::HOOK markers and writes each payload. Hooks run
rem under sh, which chokes on CRLF, so they are written with LF endings however
rem this file itself happens to be saved.
set "SELF=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; try { $lf=[string][char]10; $hooks=@{}; $order=New-Object Collections.Generic.List[string]; $name=''; foreach($line in [IO.File]::ReadAllLines($env:SELF)){ if($line -cmatch '^:::HOOK\s+(\S+)\s*$'){ $name=$matches[1]; $hooks[$name]=New-Object Collections.Generic.List[string]; $order.Add($name) } elseif($name -ne ''){ $hooks[$name].Add($line) } }; if($order.Count -eq 0){ throw 'no :::HOOK payload found in ' + $env:SELF }; foreach($k in $order){ $text=($hooks[$k] -join $lf) + $lf; $dst=Join-Path $env:HOOKDSTDIR $k; if((Test-Path -LiteralPath $dst) -and ([IO.File]::ReadAllText($dst) -ceq $text)){ '  up to date: ' + $k } else { [IO.File]::WriteAllText($dst,$text); '  installed:  ' + $k } } } catch { Write-Host $_.Exception.Message; exit 1 }"
if errorlevel 1 goto :fail

echo.
echo Done.
rem The install worked but the hooks will not run, which is worth stopping for
rem even on the happy path.
if defined WARNED pause
popd
endlocal
exit /b 0

:fail
echo.
echo Hook installation failed.
rem Most people get here by double-clicking, and a console window that closes
rem itself looks exactly like success. Hold it open so the error can be read.
pause
popd
endlocal
exit /b 1

rem ---------------------------------------------------------------------------
rem HOOK PAYLOAD
rem
rem Everything past here is data, not batch. Execution has already ended above,
rem so cmd never parses a line of it. Each ":::HOOK <name>" line names a file to
rem write into .git/hooks; the lines after it, up to the next marker or the end
rem of the file, are its contents.
rem
rem Add another hook by adding another marker. ":::HOOK" is a label as far as
rem cmd is concerned, which is why it is safe to leave lying around.
rem ---------------------------------------------------------------------------

:::HOOK pre-commit
#!/bin/sh
#
# Formats staged C# files: dotnet format, then unfugly.
# Reformatted files are re-staged, so the commit contains the formatted code.
#
# Installed by add-git-hooks.bat, which carries this script inside itself --
# edit it there, not here and not in .git/hooks, or the next install overwrites
# your changes.
#
# Skip a single commit with:  git commit --no-verify

set -e

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

# dotnet and unfugly come from PATH; add-git-hooks.bat refuses to install this
# hook unless both are there. A GUI client launched with a leaner PATH than the
# shell that installed the hook will fail here -- launch it from a shell where
# both tools work, or put their directories on the system PATH.

# Staged .cs files (added/copied/modified/renamed -- not deletions).
staged=$(git diff --cached --name-only --diff-filter=ACMR -z | tr '\0' '\n' | grep -i '\.cs$' || true)
[ -n "$staged" ] || exit 0

# A file that is both staged and dirty in the working tree cannot be formatted
# safely: re-staging it afterwards would sweep in the unstaged changes too.
partial=""
while IFS= read -r f; do
	if ! git diff --quiet -- "$f"; then
		partial="$partial  $f
"
	fi
done <<EOF
$staged
EOF
if [ -n "$partial" ]; then
	echo "pre-commit: these files are staged but also have unstaged changes:" >&2
	printf '%s' "$partial" >&2
	echo "Stage or stash the rest before committing (or use --no-verify)." >&2
	exit 1
fi

# Whatever solution this repo has, if it has one. Named nowhere, so this hook
# is the same file in every repo it is dropped into.
sln=""
for candidate in "$repo_root"/*.slnx "$repo_root"/*.sln; do
	if [ -f "$candidate" ]; then
		sln=$candidate
		break
	fi
done

if [ -n "$sln" ]; then
	include=$(printf '%s' "$staged" | tr '\n' ' ')
	echo "pre-commit: dotnet format"
	# shellcheck disable=SC2086
	dotnet format "$sln" --no-restore --include $include
fi

# unfugly last: dotnet format flattens aligned assignments, so running it
# afterwards would silently undo half of this.
echo "pre-commit: unfugly"
tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT
while IFS= read -r f; do
	[ -f "$f" ] || continue
	unfugly <"$f" >"$tmp"
	# Preserve the original file's mode/identity; only rewrite when it changed.
	if ! cmp -s "$tmp" "$f"; then
		cat "$tmp" >"$f"
	fi
done <<EOF
$staged
EOF

# shellcheck disable=SC2086
printf '%s' "$staged" | tr '\n' '\0' | xargs -0 git add --

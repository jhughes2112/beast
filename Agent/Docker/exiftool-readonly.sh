#!/bin/bash
# exiftool for readonly_bash. The real exiftool is not safe there: -if and -p evaluate Perl (a shell escape
# out of `bash -r`), -TAG=value, -o and -w write files, and ~/.ExifTool_config is Perl it loads on start.
# This wrapper lets through only whole-file reads with a fixed set of output options, then runs exiftool
# with no config file. Select tags by filtering the output (grep, jq on -j) instead of -TAG arguments.
# Absolute paths throughout: readonly_bash's PATH holds only the allowlist.

allowed=" -a -b -csv -e -ee -ee2 -ee3 -f -g -g0 -g1 -g2 -g3 -g4 -g5 -g6 -g7 -g8 -g9 -h -j -json -l -long -m -n -q -r -s -s2 -s3 -t -u -v -v0 -v1 -v2 -v3 -v4 -v5 -x -ext -fast -fast2 -fast3 -sort -struct -ver -list -listf -listr -listw -listg -lists "
withvalue=" -x -ext "

expectvalue=0
for arg in "$@"; do
	if [ "$expectvalue" = 1 ]; then
		expectvalue=0
		case "$arg" in
			-*) echo "exiftool (read-only): option value expected, got '$arg'" >&2; exit 2 ;;
		esac
		continue
	fi
	case "$arg" in
		-) ;;
		-*)
			# Exact match: exiftool's case pairs (-g/-G, -s/-S, -x/-X) are different options.
			ok=0
			case "$arg" in
				-G|-G0|-G1|-G2|-G3|-G4|-G5|-G6|-G7|-G8|-G9|-H|-S|-X|-U|-T|-D|-L) ok=1 ;;
			esac
			if [ "$ok" = 0 ] && [ "${allowed#* $arg }" != "$allowed" ]; then
				ok=1
			fi
			if [ "$ok" = 0 ]; then
				echo "exiftool (read-only): option '$arg' is not allowed here. Allowed: -G -H -S -X -U -T -D -L${allowed% }" >&2
				exit 2
			fi
			if [ "${withvalue#* $arg }" != "$withvalue" ]; then
				expectvalue=1
			fi
			;;
	esac
done
if [ "$expectvalue" = 1 ]; then
	echo "exiftool (read-only): the last option needs a value" >&2
	exit 2
fi

exec /usr/bin/exiftool -config '' "$@"

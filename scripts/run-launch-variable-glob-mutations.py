#!/usr/bin/env python3
"""Check explicit mutants at the live-variable glob boundaries."""

import argparse
import json
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "tests/ShellSyntaxTree.Tests"
GLOB_FILE = "Internal/Bash/Parsing/BashGlobPatternAnalysis.cs"
LAUNCH_FILE = "ShellLaunchEnvironment.cs"
FIELD_SPLIT = "(fragment.AllowedTransforms & ShellLexicalTransform.FieldSplit) != 0"
REPRESENTATION = "launchValue.IndexOfAny(UnsupportedHomeCharacters) < 0"
REVOKED = "!_revokedNames.Contains(name)"


def unique_span(source, text, start=0):
    position = source.index(text, start)
    if source.find(text, position + len(text)) >= 0:
        raise ValueError(f"The source marker is duplicated: {text}")
    return position, position + len(text)


def source_spans():
    glob = (ROOT / "src/ShellSyntaxTree" / GLOB_FILE).read_text()
    launch = (ROOT / "src/ShellSyntaxTree" / LAUNCH_FILE).read_text()
    gate_start, _ = unique_span(glob, "if (expansion.Kind == ShellExpansionKind.Variable &&")
    append_start, _ = unique_span(glob, "Append(builder, isWildcard, launchValue, wildcard: false);")
    literal_start = glob.index("false", append_start)
    live_guard = "if (name is not null && !_allRevoked && !_revokedNames.Contains(name) &&"
    live_start, _ = unique_span(launch, live_guard)
    revoked_start = live_start + live_guard.index(REVOKED)
    return [
        (GLOB_FILE, *unique_span(glob, FIELD_SPLIT, gate_start)),
        (GLOB_FILE, *unique_span(glob, REPRESENTATION, gate_start)),
        (GLOB_FILE, literal_start, literal_start + len("false")),
        (LAUNCH_FILE, revoked_start, revoked_start + len(REVOKED)),
    ]


def offset(source, position):
    lines = source.splitlines(keepends=True)
    return sum(map(len, lines[:position["line"] - 1])) + position["column"] - 1


def launch_expected():
    return {
        (GLOB_FILE, FIELD_SPLIT, FIELD_SPLIT.replace("!=", "==")),
        (GLOB_FILE, "fragment.AllowedTransforms & ShellLexicalTransform.FieldSplit",
         "fragment.AllowedTransforms | ShellLexicalTransform.FieldSplit"),
        (GLOB_FILE, REPRESENTATION, REPRESENTATION.replace("< 0", "> 0")),
        (GLOB_FILE, REPRESENTATION, REPRESENTATION.replace("< 0", "<= 0")),
        (GLOB_FILE, "false", "true"),
        (LAUNCH_FILE, REVOKED, REVOKED[1:]),
    }


def verify_report(report_path, spans, expected):
    report = json.loads(report_path.read_text())
    observed = {}
    outside = []
    for filename, facts in report["files"].items():
        filename = filename.replace("\\", "/")
        selected = [(start, end) for path, start, end in spans
                    if filename == path or filename.endswith("/" + path)]
        source = facts["source"]
        for mutant in facts["mutants"]:
            start = offset(source, mutant["location"]["start"])
            end = offset(source, mutant["location"]["end"])
            if not any(left <= start and end <= right for left, right in selected):
                if mutant["status"] not in {"Ignored", "CompileError"}:
                    outside.append((filename, mutant["id"], mutant["status"]))
                continue
            path = next(path for path, left, right in spans
                        if (filename == path or filename.endswith("/" + path))
                        and left <= start and end <= right)
            identity = (path, source[start:end], mutant["replacement"])
            if identity in observed:
                raise ValueError(f"A selected mutant is duplicated: {identity}")
            observed[identity] = mutant["status"]
    if outside:
        raise ValueError(f"Stryker tested mutants outside the selected spans: {outside}")
    if set(observed) != expected:
        raise ValueError(f"The selected mutants changed. Missing: {expected - set(observed)}; "
                         f"unexpected: {set(observed) - expected}")
    failures = {identity: status for identity, status in observed.items() if status != "Killed"}
    if failures:
        raise ValueError(f"The expected mutants did not die: {failures}")
    print(f"PASS: all {len(expected)} expected glob mutants were killed.")


def command_word_spans():
    glob = (ROOT / "src/ShellSyntaxTree" / GLOB_FILE).read_text()
    domain = (ROOT / "src/ShellSyntaxTree/ShellSyntaxProjection.cs").read_text()
    return [
        (GLOB_FILE, *unique_span(glob, "text.IndexOf('/') >= 0")),
        (GLOB_FILE, *unique_span(glob, "!mayStartWithDash")),
        ("ShellSyntaxProjection.cs", *unique_span(domain,
            "left.IsPathGlobWord != right.IsPathGlobWord")),
    ]


def command_word_expected():
    separator = "text.IndexOf('/') >= 0"
    equality = "left.IsPathGlobWord != right.IsPathGlobWord"
    return {
        (GLOB_FILE, separator, separator.replace(">= 0", "> 0")),
        (GLOB_FILE, separator, separator.replace(">= 0", "< 0")),
        (GLOB_FILE, "!mayStartWithDash", "mayStartWithDash"),
        ("ShellSyntaxProjection.cs", equality, equality.replace("!=", "==")),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", nargs="?", type=Path,
                        default=None)
    parser.add_argument("--command-words", action="store_true",
                        help="Check separator and all-visits command-word proof.")
    parser.add_argument("--report", type=Path,
                        help="Check an existing report without a Stryker run.")
    args = parser.parse_args()
    spans = command_word_spans() if args.command_words else source_spans()
    expected = command_word_expected() if args.command_words else launch_expected()
    scope = "glob-command-words" if args.command_words else "launch-variable-globs"
    if args.report:
        verify_report(args.report.resolve(), spans, expected)
        return
    output = (args.output or ROOT / "artifacts/stryker" / scope).resolve()
    command = ["dotnet", "stryker", "--config-file", f"stryker-{scope}.json",
               "--output", str(output), "--skip-version-check"]
    for path, start, end in spans:
        command.extend(["--mutate", f"{path}{{{start}..{end}}}"])
    subprocess.run(command, cwd=PROJECT, check=True)
    verify_report(output / "reports/mutation-report.json", spans, expected)


if __name__ == "__main__":
    main()

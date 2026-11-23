#!/usr/bin/env python3
"""Every NuGet package the server ships (direct and transitive) and its licence, from the packages' .nuspec files in
the local NuGet cache, as a Markdown table: docs/third-party-packages.md. Run from the repo root after a restore:

    python3 tools/package-licenses.py > docs/third-party-packages.md
"""
import glob
import json
import re
import subprocess

listing = json.loads(subprocess.run(
    ["dotnet", "list", "src/TomYum.Server", "package", "--include-transitive", "--format", "json"],
    capture_output=True, text=True, check=True).stdout)
cache = subprocess.run(["dotnet", "nuget", "locals", "global-packages", "-l"], capture_output=True, text=True,
                       check=True).stdout.split(":", 1)[1].strip()

packages = {}
for project in listing["projects"]:
    for framework in project.get("frameworks", []):
        for kind in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(kind, []):
                packages[package["id"]] = (package.get("resolvedVersion") or "", kind == "topLevelPackages")


def licence(package_id, version):
    nuspec = glob.glob(f"{cache}/{package_id.lower()}/{version.lower()}/*.nuspec")
    if not nuspec:
        return "not in the cache"
    text = open(nuspec[0], encoding="utf-8-sig").read()
    if m := re.search(r'<license type="expression">([^<]+)</license>', text):
        return m.group(1).strip()
    if m := re.search(r'<license type="file">([^<]+)</license>', text):
        return f"see the package's {m.group(1).strip()}"
    if m := re.search(r"<licenseUrl>([^<]+)</licenseUrl>", text):
        return m.group(1).strip()
    return "not stated"


print("# Third-party packages\n")
print("Every NuGet package the server ships and its licence, from the packages themselves "
      "(`tools/package-licenses.py`). What to watch for is in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).\n")
print("| Package | Version | Licence | |")
print("|---|---|---|---|")
for package_id, (version, direct) in sorted(packages.items(), key=lambda item: item[0].lower()):
    print(f"| {package_id} | {version} | {licence(package_id, version)} | {'direct' if direct else ''} |")

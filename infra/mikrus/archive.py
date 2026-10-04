"""Validate before extraction: regular files/directories only, bounded size, no escapes."""
import pathlib
import re
import shutil
import sys
import tarfile


def validate(archive):
    members = archive.getmembers()
    if len(members) > 20000:
        raise ValueError("Too many archive entries")
    names = set()
    total = 0
    for member in members:
        name = member.name.rstrip("/")
        parts = name.split("/")
        if (
            not re.fullmatch(r"[A-Za-z0-9_.+/-]+", name)
            or any(part in ("", ".", "..") for part in parts)
            or parts[0] not in ("api", "web")
            or name in names
            or not (member.isfile() or member.isdir())
        ):
            raise ValueError(f"Unsafe archive entry: {member.name}")
        names.add(name)
        total += member.size
    if total > 512 * 1024 * 1024:
        raise ValueError("Expanded archive exceeds 512 MiB")
    for required in ("api/PlanSafe.Api", "api/libe_sqlite3.so", "web/index.html"):
        if required not in names or not archive.getmember(required).isfile():
            raise ValueError(f"Missing regular file: {required}")
    if not any(
        re.fullmatch(r"web/_framework/dotnet(?:\.[a-z0-9]{10,64})?\.js", name)
        and archive.getmember(name).isfile()
        for name in names
    ):
        raise ValueError("Missing .NET bootstrap script in web/_framework")
    if not archive.getmember("api/PlanSafe.Api").mode & 0o111:
        raise ValueError("API is not executable")
    return total


if __name__ == "__main__":
    with tarfile.open(sys.argv[1], "r:gz") as archive:
        size = validate(archive)
        print(size)
        if len(sys.argv) == 3:
            destination = pathlib.Path(sys.argv[2])
            if not destination.is_dir() or any(destination.iterdir()):
                raise ValueError("Extraction destination must be empty")
            # Materialise only validated regular files/directories; never tar links or modes.
            for member in archive.getmembers():
                target = destination / member.name
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    source = archive.extractfile(member)
                    if source is None:
                        raise ValueError(f"Missing regular file content: {member.name}")
                    with source, target.open("xb") as output:
                        shutil.copyfileobj(source, output)
                    target.chmod(0o755 if member.name == "api/PlanSafe.Api" else 0o644)

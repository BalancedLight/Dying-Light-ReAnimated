# Material graph commands

Compare two compiled ABDM material databases and create a reviewed union in a new file.

```powershell
DLReAnimated material-graph inspect --destination ".\materials\current.mp" --source ".\materials\additions.mp"

DLReAnimated material-graph merge --destination ".\materials\current.mp" --source ".\materials\additions.mp" --output ".\materials\merged.mp" --reviewed
```

Run `inspect` first and review the reported input hashes, additions, identical records, and conflicts. Run `merge` with `--reviewed` after checking that plan.

Each input is limited to 256 MiB. The reader validates all containers, record tables, payload ranges, counts, keys, and the supported uncompressed ABDM layout.

## JSON reports

Both commands write a JSON report to standard output. Input and output identities use file names and SHA-256 hashes. Directory paths are omitted.

The `destination` and `source` objects contain `fileName`, `byteLength`, and `sha256`. The `plan` object contains:

- `canMerge`
- `addedContainers`
- `addedRecords`
- `identicalRecords`
- `conflictCount`
- `conflicts`, with each container name, hexadecimal record key, destination hash, and source hash

A successful merge also reports the output file name and hash.

## Merge rules

Existing destination records retain their logical and full stored bytes. New source containers and records are added. Duplicate records must have the same logical content; the known zero-padding layouts for `strings` and `input_attributes` are normalized for comparison. Conflicting keys are reported and prevent publication.

The output must be a new file distinct from both inputs. Publication stages the complete union, checks its bytes and graph readback, and moves it into place with overwrite disabled. Cancellation removes the staged file.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Inspection found no conflicts, merge completed, or help was displayed. |
| 2 | Conflicts, malformed data, unsupported arguments, a size limit, or an output collision. |
| 130 | The operation was cancelled. |

Conflicts produce a JSON inspection report even when `merge` was requested. Other errors are written to standard error.

Use `DLReAnimated material-graph --help` to display command syntax.

# Running app controls

Use the CLI to inspect, open projects, save, and close a running ReAnimated window.

```text
DLReAnimated app list
DLReAnimated app status --pid <pid>
DLReAnimated app open --pid <pid> --project <absolute-project.dlraproj>
DLReAnimated app save --pid <pid> --output <absolute-new-project.dlraproj>
DLReAnimated app close --pid <pid>
DLReAnimated app close --pid <pid> --output <absolute-new-project.dlraproj>
```

Each targeted command requires exactly one selector: `--pid <pid>` or
`--instance <id>`. The list response includes both selectors. Instance IDs
identify one application lifetime; PID selection also verifies the process
start time and executable before connecting.

Status returns the current authenticated app snapshot as JSON under `status`.
The existing `instanceId`, `processId`, `projectName`, `projectPath`, `isDirty`,
and `isBusy` fields are joined by the following nullable context fields:

| Field | Meaning |
|---|---|
| `activeWorkflow` | Current workspace enum name, such as `Models`, `Animations`, or `Playback`. |
| `activePage` | `Welcome`, `ModelAuthoring`, `ModelBrowser`, or the current workspace name. |
| `guidedStep` | Current guided model step when the model authoring page is visible. |
| `projectId` | Identifier of the current project. |
| `selectedModelId` | Project model entry selected in the model library. |
| `selectedAnimationId` | Animation row selected in the animation library. |
| `activeAnimationId` | Active animation identifier in the current project. |

Context fields remain null when an older app instance does not supply them.
Nullable selection identifiers also mean that the relevant library has no
selected row. Selection and active animation are reported separately because
choosing a library row does not necessarily activate it. Status is available
while work is running; check `isBusy` before issuing an authoring command.

Open requires an existing absolute `.dlraproj` path and an idle window with no
unsaved changes. It uses the Open button's transactional restore without a file
picker. A rejected or failed open keeps the current project and model session.
The response includes the opened project name/path and its current state. Save
changes first when switching projects. There is no discard or force option.

Save requires a new absolute `.dlraproj` output path. It runs the same project
persistence workflow as the Save button, including pending model integration,
model packages, and pending assets. Existing output files are rejected. After
saving, the window adopts the new project path.

New outputs can use another directory. Saving copies every local project asset
to its recorded relative path, checks its content identity, and refuses
conflicting destination bytes. Retail resource references continue to use the
game asset catalog. A failed save keeps the current project path and removes
asset files created by that save.

Close requires an idle window with all changes saved. Supplying `--output`
saves first, then checks the state again. The response contains
`closeScheduled: true` when the window has accepted the close request. The
window checks its state once more when executing the close; new edits or work
can prevent it from closing. A failed or cancelled save leaves the window open.

List and targeted responses use JSON. Exit code `0` indicates success, `2`
indicates a rejected or failed request, and `130` indicates caller cancellation.
Requests time out after two minutes; check status before retrying a timed out
save or open. A project write or restore already being committed can finish
after caller cancellation or disconnection.

The application publishes per-user discovery under its local application-data
folder and removes its entry on exit. Stale entries are ignored. Each window
uses a random pipe name and access token, a current-user-only named pipe, a
strict status/open/save/close command list, and 16 KiB message limits. Credentials
are used internally and omitted from CLI output. The endpoint starts with the
normal desktop application and stays busy through startup initialization.

"""Small Qt worker-thread bridge for long-running GUI operations."""

from __future__ import annotations

from dataclasses import dataclass
import traceback
from typing import Any, Callable

from PySide6.QtCore import QObject, QThread, Signal, Slot


@dataclass(frozen=True, slots=True)
class TaskFailure:
    message: str
    traceback: str
    exception_type: str = ""

    @classmethod
    def from_exception(cls, exc: Exception) -> "TaskFailure":
        return cls(str(exc), traceback.format_exc(), type(exc).__name__)

    def display_message(self, include_traceback_hint: bool = True) -> str:
        """Return an actionable dialog message without exposing a traceback."""

        diagnostic_suffix = (
            " The full technical traceback is in the build log."
            if include_traceback_hint
            else ""
        )

        if self.exception_type == "KeyError":
            missing = self.message.strip().strip("'\"") or "(unknown item)"
            if missing.casefold().endswith("headtop_end"):
                return (
                    "The imported skeleton does not contain the optional Head End "
                    "helper bone.\n\n"
                    f"Missing item: {missing}\n\n"
                    "HeadTop_End is a non-deforming marker above the head that some "
                    "rigs use to indicate which direction the head points. It is not "
                    "an actual body part and its absence should not block export. The "
                    "HeadTop_End can be left unmapped for this export."
                    + diagnostic_suffix
                )
            return (
                "The exporter tried to use data that is not present.\n\n"
                f"Missing item: {missing}\n\n"
                "This usually means a bone, resource, or mapping entry was treated as "
                "required even though the imported file does not contain it."
                + diagnostic_suffix
            )
        if self.message.strip():
            return self.message
        label = self.exception_type or "Unknown error"
        return (
            f"{label}: the operation failed without an explanatory message. "
            "See the log for details."
        )


class _TaskWorker(QObject):
    progress = Signal(str)
    succeeded = Signal(object)
    failed = Signal(object)
    finished = Signal()

    def __init__(self, work: Callable[[Callable[[str], None]], Any]) -> None:
        super().__init__()
        self.work = work

    @Slot()
    def run(self) -> None:
        try:
            result = self.work(self.progress.emit)
        except Exception as exc:
            self.failed.emit(TaskFailure.from_exception(exc))
        else:
            self.succeeded.emit(result)
        finally:
            self.finished.emit()


class BackgroundTaskRunner(QObject):
    """Run one callable at a time and marshal callbacks onto the GUI thread."""

    def __init__(self, parent: QObject | None = None) -> None:
        super().__init__(parent)
        self._thread: QThread | None = None
        self._worker: _TaskWorker | None = None
        self._progress_callback: Callable[[str], None] | None = None
        self._succeeded_callback: Callable[[Any], None] | None = None
        self._failed_callback: Callable[[TaskFailure], None] | None = None
        self._finished_callback: Callable[[], None] | None = None

    @property
    def busy(self) -> bool:
        return self._thread is not None

    def start(
        self,
        work: Callable[[Callable[[str], None]], Any],
        *,
        progress: Callable[[str], None] | None = None,
        succeeded: Callable[[Any], None] | None = None,
        failed: Callable[[TaskFailure], None] | None = None,
        finished: Callable[[], None] | None = None,
    ) -> bool:
        if self.busy:
            return False
        thread = QThread(self)
        worker = _TaskWorker(work)
        worker.moveToThread(thread)
        thread.started.connect(worker.run)
        self._progress_callback = progress
        self._succeeded_callback = succeeded
        self._failed_callback = failed
        self._finished_callback = finished
        worker.progress.connect(self._handle_progress)
        worker.succeeded.connect(self._handle_succeeded)
        worker.failed.connect(self._handle_failed)
        worker.finished.connect(self._handle_finished)
        worker.finished.connect(thread.quit)
        worker.finished.connect(worker.deleteLater)
        thread.finished.connect(thread.deleteLater)
        thread.finished.connect(self._clear)
        self._thread = thread
        self._worker = worker
        thread.start()
        return True

    @Slot(str)
    def _handle_progress(self, message: str) -> None:
        if self._progress_callback is not None:
            self._progress_callback(message)

    @Slot(object)
    def _handle_succeeded(self, result: Any) -> None:
        if self._succeeded_callback is not None:
            self._succeeded_callback(result)

    @Slot(object)
    def _handle_failed(self, failure: TaskFailure) -> None:
        if self._failed_callback is not None:
            self._failed_callback(failure)

    @Slot()
    def _handle_finished(self) -> None:
        if self._finished_callback is not None:
            self._finished_callback()

    @Slot()
    def _clear(self) -> None:
        self._thread = None
        self._worker = None
        self._progress_callback = None
        self._succeeded_callback = None
        self._failed_callback = None
        self._finished_callback = None


__all__ = ["BackgroundTaskRunner", "TaskFailure"]

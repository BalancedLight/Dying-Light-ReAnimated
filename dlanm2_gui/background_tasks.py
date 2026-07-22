"""Small Qt worker-thread bridge for long-running GUI operations."""

from __future__ import annotations

from dataclasses import dataclass
import threading
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


class _TaskCancelled(Exception):
    """Internal cooperative-cancellation sentinel."""


class _TaskWorker(QObject):
    progress = Signal(str)
    partial = Signal(object)
    succeeded = Signal(object)
    failed = Signal(object)
    cancelled = Signal()
    finished = Signal()

    def __init__(
        self,
        work: Callable[..., Any],
        cancel_event: threading.Event,
        *,
        streaming: bool,
    ) -> None:
        super().__init__()
        self.work = work
        self.cancel_event = cancel_event
        self.streaming = streaming

    def _report_progress(self, message: str) -> None:
        if self.cancel_event.is_set():
            raise _TaskCancelled()
        self.progress.emit(message)

    @Slot()
    def run(self) -> None:
        try:
            result = (
                self.work(self._report_progress, self.partial.emit)
                if self.streaming
                else self.work(self._report_progress)
            )
        except _TaskCancelled:
            self.cancelled.emit()
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
        self._partial_callback: Callable[[Any], None] | None = None
        self._succeeded_callback: Callable[[Any], None] | None = None
        self._failed_callback: Callable[[TaskFailure], None] | None = None
        self._cancelled_callback: Callable[[], None] | None = None
        self._finished_callback: Callable[[], None] | None = None
        self._cancel_event: threading.Event | None = None

    @property
    def busy(self) -> bool:
        return self._thread is not None

    def start(
        self,
        work: Callable[..., Any],
        *,
        progress: Callable[[str], None] | None = None,
        partial: Callable[[Any], None] | None = None,
        succeeded: Callable[[Any], None] | None = None,
        failed: Callable[[TaskFailure], None] | None = None,
        cancelled: Callable[[], None] | None = None,
        finished: Callable[[], None] | None = None,
    ) -> bool:
        if self.busy:
            return False
        thread = QThread(self)
        cancel_event = threading.Event()
        worker = _TaskWorker(work, cancel_event, streaming=partial is not None)
        worker.moveToThread(thread)
        thread.started.connect(worker.run)
        self._progress_callback = progress
        self._partial_callback = partial
        self._succeeded_callback = succeeded
        self._failed_callback = failed
        self._cancelled_callback = cancelled
        self._finished_callback = finished
        self._cancel_event = cancel_event
        worker.progress.connect(self._handle_progress)
        worker.partial.connect(self._handle_partial)
        worker.succeeded.connect(self._handle_succeeded)
        worker.failed.connect(self._handle_failed)
        worker.cancelled.connect(self._handle_cancelled)
        worker.finished.connect(thread.quit)
        worker.finished.connect(worker.deleteLater)
        thread.finished.connect(self._clear)
        thread.finished.connect(thread.deleteLater)
        self._thread = thread
        self._worker = worker
        thread.start()
        return True

    def cancel(self) -> bool:
        """Request cancellation at the worker's next safe progress checkpoint."""

        if self._cancel_event is None:
            return False
        self._cancel_event.set()
        return True

    @Slot(str)
    def _handle_progress(self, message: str) -> None:
        if self._progress_callback is not None:
            self._progress_callback(message)

    @Slot(object)
    def _handle_partial(self, result: Any) -> None:
        if self._partial_callback is not None:
            self._partial_callback(result)

    @Slot(object)
    def _handle_succeeded(self, result: Any) -> None:
        if self._succeeded_callback is not None:
            self._succeeded_callback(result)

    @Slot(object)
    def _handle_failed(self, failure: TaskFailure) -> None:
        if self._failed_callback is not None:
            self._failed_callback(failure)

    @Slot()
    def _handle_cancelled(self) -> None:
        if self._cancelled_callback is not None:
            self._cancelled_callback()

    @Slot()
    def _clear(self) -> None:
        finished_callback = self._finished_callback
        self._thread = None
        self._worker = None
        self._progress_callback = None
        self._partial_callback = None
        self._succeeded_callback = None
        self._failed_callback = None
        self._cancelled_callback = None
        self._finished_callback = None
        self._cancel_event = None
        if finished_callback is not None:
            finished_callback()


__all__ = ["BackgroundTaskRunner", "TaskFailure"]
